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
	// (the session's, see EditorSession)
	private Scene _scene { get => _session.Scene; set => _session.Scene = value; }
	private SceneState _sceneState => _session.SceneState;   // the scene playhead, selected object and clip
	private SceneObject? _active => _session.Active;         // the object open for editing (at scene level: the last one open)
	private bool _sceneLevel { get => _session.SceneLevel; set => _session.SceneLevel = value; }
	private ObjectTool _objectTool = null!;
	private SceneTimelinePanel _sceneTimeline = null!;
	private bool _objectDrag;              // the current viewport press went to the object tool
	private bool _syncingScene;
	private object? _shownObject;          // the object the properties show
	private SceneClip? _shownClip;         // the clip the properties show

	private bool Editing => _session.Editing;
	private float ClockTime => _session.ClockTime;
	private float SceneTimeNow => _session.SceneTimeNow;
	private float FrameTime => _session.FrameTime;
	private SceneObject? CurrentObject => _session.CurrentObject;

	private void InitScene()
	{
		_objectTool = new ObjectTool(() => _scene, _sceneState, _camera, _renderer, _state.Undo) { Modifiers = HeldKeys };
		_objectTool.EnterRequested += EnterObject;
		_sceneTimeline = new SceneTimelinePanel(() => _scene, _sceneState, _state) { Modifiers = HeldKeys };
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
		if (_active != obj && _paintCanvas is not null) ClosePaintEditor();   // its drawing belongs to the old object
		_session.Open(obj);
		_skeleton = obj.Skeleton;
		if (_tool is not null) _tool.BonesEnabled = BonesUsable(obj);   // a drawn object's bone is hidden (and View → Bones)
		UpdateCameraSpace();
	}

	private void EnterObject(SceneObject obj) => EnterObject(obj, null);

	// Opens an object in place: if a clip of it plays at this moment of the scene (or `via` was
	// double-clicked), its animation opens at the matching frame, and the rest of the scene
	// keeps playing along with its timeline.
	private void EnterObject(SceneObject obj, SceneClip? via)
	{
		if (_active != obj && _paintCanvas is not null) ClosePaintEditor();
		_session.Enter(obj, via);
		EditObject(obj);   // (the window's side: the paint editor, the tools, the camera)
		PoseScene(ClockTime);   // the panels show the pose you're about to edit
		UpdateCameraSpace();
		RefreshEverything();
	}

	private void LeaveObject()
	{
		if (_sceneLevel) return;
		ClosePaintEditor();
		_session.Leave();   // the scene goes on from where the timeline was
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
	private void PoseScene(float time) => _session.PoseScene(time, PoseMix);   // (the mixer playing poses the open object)
	private float WrapSceneTime(float t) => _session.WrapSceneTime(t);
	private void AdvanceScene(float dt) => _session.AdvanceScene(dt);

	private static Animation? PreviewOf(SceneObject obj) => ScenePlayer.Preview(obj);

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

	// Inside an object, a double-click on another object opens that one instead
	private bool TryEnterOtherObjectAt(Vector2 screen)
	{
		if (_objectTool.Pick(_camera.ScreenToScene(screen)) is not { } hit || hit == _active) return false;
		EnterObject(hit);
		return true;
	}

	private void StepScene(int frames) => _session.StepScene(frames);

	// ======================= Objects panel =======================

	private void RefreshScenePanel()
	{
		// An undo may have removed the open object, or the selected one
		if (_active == null || !_scene.Objects.Contains(_active)) EditObject(_scene.Objects[0]);
		_session.KeepToWhatExists();   // the selected object and clip too

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

	// Scene edits as undo steps (see EditorSession.EditScene)
	private bool EditStructureAndScene(Func<string?> change) => _session.EditStructureAndScene(change);
	private bool EditScene(Func<string?> change) => _session.EditScene(change);

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
			"A drawn object has no bones: just parts you paint, frame by frame if you like. " +
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
		var (created, part, drawing) = _session.AddDrawnObject(name, pixels, width, height, _camera.Pan);
		_state.Playing = false;
		EnterObject(created);
		_state.Selected = part.Bone;
		_state.SelectedSlot = part;
		RefreshRigPanel();
		if (paint) OpenPaintEditor(drawing, part);
	}

	// A new rig is there to be built: it opens, in the middle of the view
	private void AddRiggedObject() => EnterObject(_session.AddRiggedObject(_camera.Pan));

	private void CopyObject_Click(object sender, RoutedEventArgs e)
	{
		if (CurrentObject is not { } source) return;
		_session.FinishPaintTool();   // a half-placed move or figure lands first, so the copy has it
		var copy = _session.CopyObject(source);
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
		bool open = obj == _active;
		if (open && _scene.Objects.Count > 1) ClosePaintEditor();   // its drawing goes with it
		if (!_session.DeleteObject(obj)) return;
		if (open) EditObject(_active!);   // (the window's side of the object the session opened instead)
		RefreshEverything();
	}

	private void ObjectUp_Click(object sender, RoutedEventArgs e) => MoveObjectInOrder(+1);
	private void ObjectDown_Click(object sender, RoutedEventArgs e) => MoveObjectInOrder(-1);

	private void MoveObjectInOrder(int direction)
	{
		if (CurrentObject is { } obj) _session.MoveObjectInOrder(obj, direction);
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
			if (!_session.RenameObject(obj, newName))
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

		switch (field)
		{
			case "OX": _session.PlaceObject(obj, position: new Vector2(v, obj.Position.Y)); break;
			case "OY": _session.PlaceObject(obj, position: new Vector2(obj.Position.X, v)); break;
			case "ORotation": _session.PlaceObject(obj, rotation: Rad(v)); break;
			case "OScaleX": _session.PlaceObject(obj, scale: new Vector2(v, obj.Scale.Y)); break;
			case "OScaleY": _session.PlaceObject(obj, scale: new Vector2(obj.Scale.X, v)); break;
		}
	}

	private void ObjectFlags_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingScene || _objectTool.Selected is not { } obj) return;
		_session.SetObjectFlags(obj, ObjectVisible.IsChecked == true, ObjectLocked.IsChecked == true,
			ObjectExport.IsChecked == true, ObjectOnion.IsChecked == true);
	}

	// "Free", then every bone this object could hang on (see EditorSession.AttachChoices)
	private List<AttachChoice> AttachChoices(SceneObject obj) => _session.AttachChoices(obj);

	private void ObjectAttach_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingScene || _objectTool.Selected is not { } obj || ObjectAttach.SelectedItem is not AttachChoice choice) return;
		if (!_session.AttachObject(obj, choice)) ShowObjectProperties();   // put the choice back to what's really there
	}

	private void ObjectPreview_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingScene || _objectTool.Selected is not { } obj || ObjectPreview.SelectedItem is not string name) return;
		_session.SetPreview(obj, name);
	}
}
