using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;

namespace Animator.Viewer;

// The scene: every object in the project, and the Objects panel.
//
// One object is open for editing at a time. Its rig is _skeleton (and _state.Skeleton), its
// animations are _state.Animations, and every rig panel, tool and the timeline work on those.
// Opening another object just points them somewhere else: each object keeps its own rig.
// At scene level (Esc) the viewport moves whole objects instead, and the bottom panel is the
// scene timeline: every object plays its clips there (one with no clips loops its preview).
public partial class MainWindow
{
	private Scene _scene = new();
	private readonly SceneState _sceneState = new();   // the scene playhead, selected object and clip
	private SceneObject? _active;          // the object open for editing (at scene level: the last one open)
	private bool _sceneLevel;              // true = working on whole objects
	private float _sceneTimeAtZero;        // inside an object: the scene time its timeline's 0 stands for
	private ObjectTool _objectTool = null!;
	private SceneTimelinePanel _sceneTimeline = null!;
	private bool _objectDrag;              // the current viewport press went to the object tool
	private bool _syncingScene;
	private object? _shownObject;          // the object the properties show
	private SceneClip? _shownClip;         // the clip the properties show
	private const float FadedOpacity = 0.35f;

	private bool Editing => !_sceneLevel && _active != null;

	// The time the panels and tools work at: the timeline's inside an object, the scene playhead outside
	private float ClockTime => _sceneLevel ? _sceneState.Time : _state.Time;

	// The scene time everything else is shown at. Inside an object it follows the object's
	// timeline, so the rest of the scene plays along in context.
	private float SceneTimeNow => _sceneLevel ? _sceneState.Time : _sceneTimeAtZero + _state.Time;

	// One frame, in seconds
	private float FrameTime => 1f / Math.Max(1, _state.Fps);

	// The object the panel's buttons work on: the selected one at scene level, else the open one
	private SceneObject? CurrentObject => _sceneLevel ? _objectTool.Selected : _active;

	private void InitScene()
	{
		_objectTool = new ObjectTool(() => _scene, _sceneState, _camera, _renderer, _state.Undo);
		_objectTool.EnterRequested += EnterObject;
		_sceneTimeline = new SceneTimelinePanel(() => _scene, _sceneState, _state);
		_sceneTimeline.ClipOpened += (obj, clip) => EnterObject(obj, clip);
		_sceneTimeline.MenuRequested += ShowClipMenu;
		// Subscribed before the other panels: an undo can remove the open object, and they
		// must refresh with whichever object is open afterwards
		_state.Undo.Changed += RefreshScenePanel;
		RefreshScenePanel();
	}

	// A scene with just the rig in _skeleton and its animations (the window opening, or File > New)
	private Scene SingleObjectScene()
	{
		var scene = new Scene { Fps = _state.Fps };
		scene.Objects.Add(new SceneObject("Character", ObjectKind.Rigged, _skeleton) { Animations = _state.Animations });
		return scene;
	}

	// ======================= opening objects =======================

	// Points the editor at this object's rig and animations
	private void EditObject(SceneObject obj)
	{
		if (obj.Animations.Count == 0)
			obj.Animations.Add(new Animation { Name = "animation", Duration = 2f, Loop = true });

		bool switching = _active != obj;
		if (switching)
		{
			if (_paint is not null) ClosePaintEditor();   // its drawing belongs to the old object
			_state.Selected = null;
			_state.SelectedSlot = null;
			_state.SelectedKeys.Clear();
		}

		_active = obj;
		_skeleton = obj.Skeleton;
		_state.Skeleton = obj.Skeleton;
		_state.Animations = obj.Animations;
		if (switching || !obj.Animations.Contains(_state.Animation))
		{
			_state.Animation = PreviewOf(obj) ?? obj.Animations[0];
			_state.SetFrame(_state.Frame);   // keeps the playhead inside the animation
		}
		if (_paint is not null) _paint.Skeleton = obj.Skeleton;
		if (_tool is not null) _tool.BonesEnabled = obj.Kind != ObjectKind.Drawn;   // a drawn object's bone is hidden
		UpdateCameraSpace();
	}

	private void EnterObject(SceneObject obj) => EnterObject(obj, null);

	// Opens an object in place: if a clip of it plays at this moment of the scene (or `via` was
	// double-clicked), its animation opens at the matching frame, and the rest of the scene
	// keeps playing along with its timeline.
	private void EnterObject(SceneObject obj, SceneClip? via)
	{
		float sceneTime = SceneTimeNow;
		if (via != null && !via.Covers(sceneTime)) sceneTime = via.Start;   // opened away from the playhead
		var clip = via ?? (obj.ClipAt(sceneTime) is { } playing && playing.Covers(sceneTime) ? playing : null);

		_sceneLevel = false;
		EditObject(obj);
		if (clip != null && obj.Animations.Contains(clip.Animation))
		{
			_state.Animation = clip.Animation;
			_state.Time = MathF.Round(clip.AnimationTime(sceneTime) * _state.Fps) / _state.Fps;
			_state.SelectedKeys.Clear();
		}
		_sceneTimeAtZero = sceneTime - _state.Time;
		_objectTool.Selected = obj;
		PoseScene(ClockTime);   // the panels show the pose you're about to edit
		UpdateCameraSpace();
		RefreshEverything();
	}

	private void LeaveObject()
	{
		if (_sceneLevel) return;
		ClosePaintEditor();
		_sceneState.Time = WrapSceneTime(SceneTimeNow);   // the scene goes on from where the timeline was
		_sceneLevel = true;
		_objectTool.Selected = _active;
		_state.Selected = null;
		_state.SelectedSlot = null;
		UpdateCameraSpace();
		RefreshEverything();
	}

	private void RefreshEverything()
	{
		RefreshScenePanel();
		RefreshRigPanel();
		RefreshAnimationsPanel();
		UpdateStatus();
	}

	// Tools work in the open object's own space; at scene level, in the scene's
	private void UpdateCameraSpace() =>
		_camera.Space = Editing ? _scene.WorldTransform(_active!) : Matrix3x2.Identity;

	// ======================= posing & drawing =======================

	// Every object at one time (ClockTime's kind): the open one with the animation being edited,
	// the others (at scene level, all of them) as the scene timeline has them then
	private void PoseScene(float time)
	{
		float sceneTime = Editing ? _sceneTimeAtZero + time : time;
		foreach (var obj in _scene.Objects)
		{
			if (Editing && obj == _active)
			{
				if (PoseMix()) continue;   // the mixer playing
				PoseAt(time);
				if (_state.AnimateMode) _skeleton.StepPhysics(time);   // jiggles swing as time goes by
				else _skeleton.ResetPhysics();
			}
			else ScenePlayer.Pose(obj, sceneTime);   // its clip there, or its preview animation looping
		}
	}

	// Scene time kept inside the scene: wrapped when it loops, held at the end when it doesn't
	private float WrapSceneTime(float t)
	{
		float d = _scene.Duration;
		if (d <= 0) return 0;
		if (t >= 0 && t <= d) return t;
		return _scene.Loop ? ((t % d) + d) % d : Math.Clamp(t, 0, d);
	}

	// Playing at scene level: the scene playhead runs, and loops or stops at the scene's end
	private void AdvanceScene(float dt)
	{
		float t = _sceneState.Time + dt;
		if (t > _scene.Duration && !_scene.Loop)
		{
			t = _scene.Duration;
			_state.Playing = false;
		}
		_sceneState.Time = WrapSceneTime(t);
	}

	private static Animation? PreviewOf(SceneObject obj) => ScenePlayer.Preview(obj);

	// The canvas is in scene space here. Objects are drawn in list order (first = furthest back);
	// while you edit one, the others are faded and its bones and handles go on top of everything.
	private void DrawScene(SKCanvas canvas)
	{
		// The axes the panel's numbers are measured from: the open object's origin, or the scene's
		DrawInSpace(canvas, Editing ? _camera.Space : Matrix3x2.Identity, () => _renderer.DrawAxes(canvas));
		DrawGrid(canvas);   // in the same units, behind everything
		if (Editing) DrawStage(canvas);   // inside an object: just the frame's outline, behind
		DrawOnionGhosts(canvas);

		foreach (var obj in _scene.Objects)
		{
			bool open = Editing && obj == _active;
			if (!obj.Visible && !open) continue;   // a hidden object still shows while you work on it
			DrawInSpace(canvas, _scene.WorldTransform(obj), () =>
			{
				if (Editing && !open) _renderer.DrawFaded(canvas, obj.Skeleton, FadedOpacity);
				else _renderer.DrawParts(canvas, obj.Skeleton);
				if (!Editing && obj.Kind == ObjectKind.Rigged && _renderer.ImageBounds(obj.Skeleton) == null)
					_renderer.DrawBoneHints(canvas, obj.Skeleton);   // a rig with no pictures yet
			});
		}

		if (Editing)
		{
			DrawInSpace(canvas, _camera.Space, () => _renderer.DrawOverlays(canvas, _skeleton));
			DrawHitboxes(canvas);
			DrawMeshOverlay(canvas);
			return;
		}

		DrawStage(canvas);   // at scene level, what's outside the frame is dimmed

		// Scene level: every object's origin as a faint cross (so even an empty one can be found)
		foreach (var obj in _scene.Objects.Where(o => o.Visible))
			ObjectGizmo.DrawOrigin(canvas, Vector2.Transform(Vector2.Zero, _scene.WorldTransform(obj)), _camera.Zoom);

		if (_objectTool.Selected is { Visible: true } selected && _scene.Objects.Contains(selected))
		{
			ObjectGizmo.Draw(canvas, ObjectGizmo.For(_scene, selected, _renderer, _camera.Zoom), _camera.Zoom,
							 handles: !selected.Locked);
			// Carried by a bone: a dashed line from that bone's joint to the object's origin
			if (_scene.AttachmentBone(selected) is { } bone && _scene.Find(selected.AttachObject!) is { } holder)
				ObjectGizmo.DrawAttachLine(canvas,
					Vector2.Transform(bone.World.Translation, _scene.WorldTransform(holder)),
					Vector2.Transform(Vector2.Zero, _scene.WorldTransform(selected)), _camera.Zoom);
		}
	}

	// The stage: the frame the scene is made for (an animation project's video size), centred on
	// the scene's origin. At scene level everything outside it is dimmed.
	private void DrawStage(SKCanvas canvas)
	{
		if (!_scene.HasStage) return;
		float w = _scene.StageWidth / 2f, h = _scene.StageHeight / 2f, px = 1f / _camera.Zoom, far = 1e6f;
		if (!Editing)
		{
			using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 100) };
			canvas.DrawRect(new SKRect(-far, h, far, far), dim);     // above (y points up)
			canvas.DrawRect(new SKRect(-far, -far, far, -h), dim);   // below
			canvas.DrawRect(new SKRect(-far, -h, -w, h), dim);       // left
			canvas.DrawRect(new SKRect(w, -h, far, h), dim);         // right
		}
		using var edge = new SKPaint
		{
			Color = new SKColor(255, 255, 255, Editing ? (byte)45 : (byte)120),
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 1.5f * px,
			IsAntialias = true
		};
		canvas.DrawRect(new SKRect(-w, -h, w, h), edge);
	}

	// F: zoom 1, centred on the open object's origin. At scene level: the whole stage in view
	// (or zoom 1 on the scene's origin, without a stage).
	private void ResetView()
	{
		_camera.Reset();
		if (Editing)
		{
			_camera.Pan = _camera.Space.Translation;
		}
		else if (_scene.HasStage && _camera.ViewSize.X > 0 && _camera.ViewSize.Y > 0)
		{
			float fit = MathF.Min(_camera.ViewSize.X / _scene.StageWidth, _camera.ViewSize.Y / _scene.StageHeight);
			_camera.Zoom = Math.Clamp(fit * 0.9f, 0.1f, 20f);
		}
	}

	// Draws in an object's space. The renderer keeps lines and handles the same size on screen.
	private void DrawInSpace(SKCanvas canvas, Matrix3x2 space, Action draw)
	{
		var m = SkeletonRenderer.ToSk(space);
		canvas.Save();
		canvas.Concat(in m);
		_renderer.Zoom = _camera.Zoom * ScaleOf(space);
		draw();
		canvas.Restore();
		_renderer.Zoom = _camera.Zoom;
	}

	private static float ScaleOf(Matrix3x2 m)
	{
		float det = MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21);
		return det > 1e-12f ? MathF.Sqrt(det) : 1f;
	}

	// Inside an object, a double-click on another object opens that one instead
	private bool TryEnterOtherObjectAt(Vector2 screen)
	{
		if (_objectTool.Pick(_camera.ScreenToScene(screen)) is not { } hit || hit == _active) return false;
		EnterObject(hit);
		return true;
	}

	private void StepScene(int frames)
	{
		_state.Playing = false;
		int last = (int)MathF.Round(_scene.Duration * _state.Fps);
		int frame = (int)MathF.Round(_sceneState.Time * _state.Fps) + frames;
		_sceneState.Time = Math.Clamp(frame, 0, last) / (float)_state.Fps;
	}

	// ======================= Objects panel =======================

	private void RefreshScenePanel()
	{
		// An undo may have removed the open object, or the selected one
		if (_active == null || !_scene.Objects.Contains(_active)) EditObject(_scene.Objects[0]);
		if (_objectTool.Selected != null && !_scene.Objects.Contains(_objectTool.Selected)) _objectTool.Selected = null;
		if (_sceneState.SelectedClip is { } picked && _objectTool.Selected?.Clips.Contains(picked) != true)
			_sceneState.SelectedClip = null;   // undone, or its object isn't selected any more

		// The list is only rebuilt when something in it changed, so picking with the arrow keys keeps working
		_syncingScene = true;
		var wanted = _scene.Objects.AsEnumerable().Reverse().Select(ObjectItem).ToList();   // front first, like layers
		bool same = ObjectList.Items.Count == wanted.Count && wanted.Select((w, i) =>
			ObjectList.Items[i] is ListBoxItem old && old.Tag == w.Tag && Equals(old.Content, w.Content)
			&& old.FontWeight == w.FontWeight && old.Foreground == w.Foreground).All(ok => ok);
		if (!same)
		{
			ObjectList.Items.Clear();
			foreach (var item in wanted) ObjectList.Items.Add(item);
		}
		ObjectList.SelectedItem = ObjectList.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == CurrentObject);
		_syncingScene = false;

		// Scene level: object settings instead of the rig, and no timeline (it belongs to one object)
		RigSection.Visibility = _sceneLevel ? Visibility.Collapsed : Visibility.Visible;
		ObjectProps.Visibility = _sceneLevel ? Visibility.Visible : Visibility.Collapsed;
		// The bottom panel: the open object's animations, or at scene level the scene timeline
		AnimControls.Visibility = _sceneLevel ? Visibility.Collapsed : Visibility.Visible;
		SceneControls.Visibility = _sceneLevel ? Visibility.Visible : Visibility.Collapsed;
		ShowSceneSettings();

		SceneButton.Content = _sceneLevel ? "Edit ▸" : $"◂ Scene ({KeyOf("edit.back")})";
		SceneButton.IsEnabled = !_sceneLevel || _objectTool.Selected != null;
		ModeText.Text = _sceneLevel
			? $"Scene: {MoveDrag} objects to place them. Double-click one (or {KeyOf("scene.open")}) to edit it."
			: $"Editing \"{_active?.Name}\". Other objects are faded. {KeyOf("edit.back")} = back to the scene.";

		var current = CurrentObject;
		DeleteObjectButton.IsEnabled = current != null && _scene.Objects.Count > 1;
		CopyObjectButton.IsEnabled = ObjectUpButton.IsEnabled = ObjectDownButton.IsEnabled = current != null;
		ShowObjectProperties();
	}

	// One line of the list: name, kind, and whether it's hidden or locked. The open object is bold.
	private ListBoxItem ObjectItem(SceneObject obj)
	{
		string kind = obj.Kind == ObjectKind.Drawn ? "drawn" : "rig";
		string flags = (obj.Visible ? "" : ", hidden") + (obj.Locked ? ", locked" : "");
		return new ListBoxItem
		{
			Content = $"{obj.Name}   ({kind}{flags})",
			Tag = obj,
			Foreground = obj.Visible ? Themes.Brush("Text") : Themes.Brush("TextDisabled"),
			FontWeight = obj == _active && !_sceneLevel ? FontWeights.Bold : FontWeights.Normal
		};
	}

	// Called every frame: follows clicks in the viewport, and shows live values during drags
	private void SyncScenePanel()
	{
		if (!_sceneLevel) return;
		if (!Equals(_objectTool.Selected, _shownObject) || _sceneState.SelectedClip != _shownClip) RefreshScenePanel();
		else RefreshObjectValues();
	}

	private void ObjectList_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingScene || ObjectList.SelectedItem is not ListBoxItem { Tag: SceneObject obj }) return;
		if (_sceneLevel)
		{
			_objectTool.Selected = obj;   // the panel follows on the next frame
			UpdateStatus();
		}
		else if (obj != _active)
		{
			// Inside an object, picking another one opens it. Later, so the list isn't
			// rebuilt in the middle of its own selection change.
			Dispatcher.InvokeAsync(() => EnterObject(obj));
		}
	}

	private void ObjectList_DoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (ObjectList.SelectedItem is ListBoxItem { Tag: SceneObject obj })
			Dispatcher.InvokeAsync(() => EnterObject(obj));
	}

	private void SceneButton_Click(object sender, RoutedEventArgs e)
	{
		if (!_sceneLevel) LeaveObject();
		else if (_objectTool.Selected is { } obj) EnterObject(obj);
	}

	// A rig edit that can also change the scene (objects carried by a bone that's renamed or
	// deleted), as one undo step. The change returns the step's name, or null if nothing changed.
	private bool EditStructureAndScene(Func<string?> change)
	{
		var sceneBefore = SceneSnapshot.Capture(_scene);
		var rigBefore = _state.CaptureStructure();
		string? name = change();
		if (name == null)
		{
			rigBefore.Restore();
			sceneBefore.Restore();
			return false;
		}
		_state.Undo.Push(new CompositeCommand(name,
			new SnapshotCommand<StructureSnapshot>(name, rigBefore, _state.CaptureStructure(), s => s.Restore()),
			new SnapshotCommand<SceneSnapshot>(name, sceneBefore, SceneSnapshot.Capture(_scene), s => s.Restore())));
		return true;
	}

	// One-shot scene edit as one undo step; the change returns the step's name, or null if nothing changed
	private bool EditScene(Func<string?> change)
	{
		var before = SceneSnapshot.Capture(_scene);
		string? name = change();
		if (name == null)
		{
			before.Restore();
			return false;
		}
		_state.Undo.Push(new SnapshotCommand<SceneSnapshot>(name, before, SceneSnapshot.Capture(_scene), s => s.Restore()));
		return true;
	}

	private void AddObject_Click(object sender, RoutedEventArgs e)
	{
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };

		var rigged = new MenuItem { Header = "Rigged object (bones)" };
		rigged.Click += (_, _) => AddRiggedObject();
		menu.Items.Add(rigged);

		var blank = new MenuItem { Header = "Drawn object (blank)…" };
		blank.Click += (_, _) => AddBlankDrawnObject();
		menu.Items.Add(blank);

		var image = new MenuItem { Header = "Drawn object from image…" };
		image.Click += (_, _) => AddDrawnObjectFromImage();
		menu.Items.Add(image);

		var photoshop = new MenuItem { Header = "Object from a Photoshop file…" };
		photoshop.Click += (_, _) => ImportFile(ImportKind.Photoshop);
		menu.Items.Add(photoshop);

		var animate = new MenuItem { Header = "Object from an Animate project…" };
		animate.Click += (_, _) => ImportFile(ImportKind.Animate);
		menu.Items.Add(animate);

		var sheet = new MenuItem { Header = "Objects from a sprite sheet…" };
		sheet.Click += (_, _) => ImportFile(ImportKind.SpriteSheet);
		menu.Items.Add(sheet);

		menu.IsOpen = true;
	}

	private void AddBlankDrawnObject()
	{
		var dialog = new NewPartDialog("New Drawn Object", "drawing",
			"A drawn object has no bones: just parts (layers) you paint, frame by frame if you like. " +
			"It starts with one empty part this size; add more parts inside it.", 256, 256) { Owner = this };
		if (dialog.ShowDialog() != true) return;
		AddDrawnObject(dialog.PartName, null, dialog.PartWidth, dialog.PartHeight, paint: true);
	}

	private void AddDrawnObjectFromImage()
	{
		if (PickImage() is not { } image) return;
		using var bitmap = image.Bitmap;
		AddDrawnObject(image.Name, bitmap, bitmap.Width, bitmap.Height, paint: false);
	}

	// A drawn object with one part, centred in the view, opened with that part selected
	// (and, for a blank one, in the paint editor)
	private void AddDrawnObject(string name, SKBitmap? pixels, int width, int height, bool paint)
	{
		SceneObject? created = null;
		Slot? part = null;
		Asset? drawing = null;
		EditScene(() =>
		{
			created = SceneOps.AddDrawn(_scene, name, _camera.Pan);
			drawing = new Asset { Name = name, Width = width, Height = height };
			_assets.AddLayer(drawing, "Base", pixels);   // null pixels = a blank layer
			part = PartOps.CreatePart(created.Skeleton, created.Skeleton.Roots[0], drawing, name, Vector2.Zero, 0f);
			return $"New drawn object {created.Name}";
		});
		if (created == null || part == null || drawing == null) return;

		_state.Playing = false;
		EnterObject(created);
		_state.Selected = part.Bone;
		_state.SelectedSlot = part;
		RefreshRigPanel();
		if (paint) OpenPaintEditor(drawing, part);
	}

	private void AddRiggedObject()
	{
		SceneObject? created = null;
		EditScene(() =>
		{
			created = SceneOps.AddRigged(_scene, "object", _camera.Pan);   // in the middle of the view
			return $"New object {created.Name}";
		});
		if (created != null) EnterObject(created);   // a new rig is there to be built: open it
	}

	private void CopyObject_Click(object sender, RoutedEventArgs e)
	{
		if (CurrentObject is not { } source) return;
		_paint.Tool.Deactivate(_paint);   // a half-placed move or figure lands first, so the copy has it
		SceneObject? copy = null;
		EditScene(() =>
		{
			var (made, layers) = SceneOps.Duplicate(_scene, source);
			foreach (var (newLayer, oldLayer) in layers) _assets.CopyPixels(oldLayer, newLayer);
			made.Position += new Vector2(30, -30);   // a little aside, so you can see it
			copy = made;
			return $"Copy object {source.Name}";
		});
		if (copy == null) return;
		if (_sceneLevel)
		{
			_objectTool.Selected = copy;
			RefreshScenePanel();
		}
		else
		{
			EnterObject(copy);
		}
	}

	private void DeleteObject_Click(object sender, RoutedEventArgs e) => DeleteCurrentObject();

	private void DeleteCurrentObject()
	{
		if (CurrentObject is not { } obj) return;
		bool deleted = EditScene(() => SceneOps.DeleteObject(_scene, obj) ? $"Delete object {obj.Name}" : null);
		if (!deleted) return;
		_objectTool.Selected = null;
		if (obj == _active) EditObject(_scene.Objects[0]);
		RefreshEverything();
	}

	private void ObjectUp_Click(object sender, RoutedEventArgs e) => MoveObjectInOrder(+1);
	private void ObjectDown_Click(object sender, RoutedEventArgs e) => MoveObjectInOrder(-1);

	private void MoveObjectInOrder(int direction)
	{
		if (CurrentObject is not { } obj) return;
		EditScene(() => SceneOps.MoveInOrder(_scene, obj, direction)
			? $"{obj.Name} {(direction > 0 ? "forward" : "backward")}"
			: null);
	}

	// ======================= object properties (scene level) =======================

	private void EditSelected_Click(object sender, RoutedEventArgs e)
	{
		if (_objectTool.Selected is { } obj) EnterObject(obj);
	}

	private void ShowObjectProperties()
	{
		_shownObject = _objectTool.Selected;
		var obj = _objectTool.Selected;
		ObjectFields.IsEnabled = obj != null;
		ObjectHint.Text = "";

		_syncingScene = true;
		ObjectTitle.Text = obj == null
			? "Click an object in the viewport, or pick it in the list."
			: $"{(obj.Kind == ObjectKind.Drawn ? "Drawn object" : "Rigged object")}: " +
			  $"{obj.Skeleton.Slots.Count} part(s), {obj.Animations.Count} animation(s)";
		SetField(ObjectName, obj?.Name ?? "");
		ObjectVisible.IsChecked = obj?.Visible ?? false;
		ObjectLocked.IsChecked = obj?.Locked ?? false;
		ObjectExport.IsChecked = obj?.Export ?? false;
		ObjectOnion.IsChecked = obj?.Onion ?? false;

		ObjectPreview.ItemsSource = obj?.Animations.Select(a => a.Name).ToList() ?? new List<string>();
		ObjectPreview.SelectedItem = obj == null ? null : PreviewOf(obj)?.Name;

		// What carries it (a bone that no longer exists counts as nothing)
		var attach = obj != null ? AttachChoices(obj) : new List<AttachChoice>();
		string? holderName = obj != null && _scene.AttachmentBone(obj) != null ? obj.AttachObject : null;
		string? boneName = holderName != null ? obj!.AttachBone : null;
		var carrier = attach.FirstOrDefault(c => holderName != null && c.Holder?.Name == holderName && c.Bone == boneName);
		ObjectAttach.ItemsSource = attach;
		ObjectAttach.SelectedItem = obj == null ? null : carrier ?? attach[0];
		if (carrier != null)
			ObjectTitle.Text += $"\nCarried by {carrier.Label}: X / Y, rotation and scale are measured from it.";
		_syncingScene = false;

		RefreshObjectValues();
		ShowClipProperties();
	}

	private void RefreshObjectValues()
	{
		var obj = _objectTool.Selected;
		SetField(ObjectX, obj != null ? Num(obj.Position.X) : "");
		SetField(ObjectY, obj != null ? Num(obj.Position.Y) : "");
		SetField(ObjectRotation, obj != null ? Num(Deg(obj.Rotation)) : "");
		SetField(ObjectScaleX, obj != null ? Num(obj.Scale.X) : "");
		SetField(ObjectScaleY, obj != null ? Num(obj.Scale.Y) : "");
		RefreshClipValues();
	}

	private void ObjectField_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitObjectField((TextBox)sender);
		Viewport.Focus();
	}

	private void ObjectField_LostFocus(object sender, RoutedEventArgs e) => CommitObjectField((TextBox)sender);

	private void CommitObjectField(TextBox box)
	{
		if (_syncingScene || _objectTool.Selected is not { } obj) return;
		string field = (string)box.Tag;
		ObjectHint.Text = "";

		if (field == "OName")
		{
			string newName = box.Text.Trim();
			if (newName == obj.Name) return;
			if (!EditScene(() => SceneOps.RenameObject(_scene, obj, newName) ? $"Rename object to {newName}" : null))
			{
				ObjectHint.Text = $"Can't rename to \"{newName}\": object names must be unique and not empty.";
				box.Text = obj.Name;
			}
			return;
		}

		if (!TryNum(box.Text, out float v))
		{
			ObjectHint.Text = $"\"{box.Text}\" isn't a number.";
			box.Text = "";
			RefreshObjectValues();
			return;
		}

		EditScene(() =>
		{
			switch (field)
			{
				case "OX" when v != obj.Position.X: obj.Position = new Vector2(v, obj.Position.Y); break;
				case "OY" when v != obj.Position.Y: obj.Position = new Vector2(obj.Position.X, v); break;
				case "ORotation" when MathF.Abs(Rad(v) - obj.Rotation) > 1e-6f: obj.Rotation = Rad(v); break;
				case "OScaleX" when v != obj.Scale.X: obj.Scale = new Vector2(v, obj.Scale.Y); break;
				case "OScaleY" when v != obj.Scale.Y: obj.Scale = new Vector2(obj.Scale.X, v); break;
				default: return null;
			}
			return $"Set object {obj.Name} {field[1..]}";
		});
	}

	private void ObjectFlags_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingScene || _objectTool.Selected is not { } obj) return;
		bool visible = ObjectVisible.IsChecked == true, locked = ObjectLocked.IsChecked == true;
		bool export = ObjectExport.IsChecked == true, onion = ObjectOnion.IsChecked == true;
		EditScene(() =>
		{
			if (obj.Visible == visible && obj.Locked == locked && obj.Export == export && obj.Onion == onion) return null;
			obj.Visible = visible;
			obj.Locked = locked;
			obj.Export = export;
			obj.Onion = onion;
			return $"Settings of object {obj.Name}";
		});
	}

	private sealed record AttachChoice(SceneObject? Holder, string? Bone, string Label)
	{
		public override string ToString() => Label;
	}

	// "Free", then every bone this object could hang on: not its own, and not those of objects
	// it carries (they'd end up carrying each other). A drawn object counts as one bone.
	private List<AttachChoice> AttachChoices(SceneObject obj)
	{
		var list = new List<AttachChoice> { new(null, null, "(nothing: free in the scene)") };
		foreach (var other in _scene.Objects)
		{
			if (other == obj || SceneOps.Carries(_scene, obj, other)) continue;
			if (other.Kind == ObjectKind.Drawn)
			{
				if (other.Skeleton.Roots.FirstOrDefault() is { } root) list.Add(new(other, root.Name, other.Name));
				continue;
			}
			foreach (var bone in other.Skeleton.AllBones())
				list.Add(new(other, bone.Name, $"{other.Name} ▸ {bone.Name}"));
		}
		return list;
	}

	private void ObjectAttach_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingScene || _objectTool.Selected is not { } obj || ObjectAttach.SelectedItem is not AttachChoice choice) return;
		bool changed = EditScene(() => SceneOps.Attach(_scene, obj, choice.Holder, choice.Bone)
			? (choice.Holder == null ? $"Free {obj.Name}" : $"Attach {obj.Name} to {choice.Label}")
			: null);
		if (!changed) ShowObjectProperties();   // put the choice back to what's really there
	}

	private void ObjectPreview_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingScene || _objectTool.Selected is not { } obj || ObjectPreview.SelectedItem is not string name) return;
		EditScene(() =>
		{
			if (PreviewOf(obj)?.Name == name) return null;
			obj.PreviewAnimation = name;
			return $"{obj.Name} previews {name}";
		});
	}
}
