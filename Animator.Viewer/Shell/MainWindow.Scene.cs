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
// At scene level (Esc) the viewport moves whole objects instead, and every object plays its
// preview animation on the scene's own clock.
public partial class MainWindow
{
	private Scene _scene = new();
	private SceneObject? _active;          // the object open for editing (at scene level: the last one open)
	private bool _sceneLevel;              // true = working on whole objects
	private float _sceneTime;              // the clock at scene level; each preview loops on its own
	private ObjectTool _objectTool = null!;
	private bool _objectDrag;              // the current viewport press went to the object tool
	private bool _syncingScene;
	private object? _shownObject;          // the object the properties show
	private const float FadedOpacity = 0.35f;

	private bool Editing => !_sceneLevel && _active != null;

	// The time everything is shown at: the timeline's inside an object, the scene clock outside
	private float ClockTime => _sceneLevel ? _sceneTime : _state.Time;

	// The object the panel's buttons work on: the selected one at scene level, else the open one
	private SceneObject? CurrentObject => _sceneLevel ? _objectTool.Selected : _active;

	private void InitScene()
	{
		_objectTool = new ObjectTool(() => _scene, _camera, _renderer, _state.Undo);
		_objectTool.EnterRequested += EnterObject;
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
		UpdateCameraSpace();
	}

	private void EnterObject(SceneObject obj)
	{
		_sceneLevel = false;
		EditObject(obj);
		_objectTool.Selected = obj;
		UpdateCameraSpace();
		RefreshEverything();
	}

	private void LeaveObject()
	{
		if (_sceneLevel) return;
		ClosePaintEditor();
		_sceneLevel = true;
		_sceneTime = _state.Time;   // the scene goes on from where the timeline was
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

	// Every object at one time: the open one with the animation being edited, the others
	// (at scene level, all of them) with their preview animation
	private void PoseScene(float time)
	{
		foreach (var obj in _scene.Objects)
		{
			if (Editing && obj == _active)
			{
				PoseAt(time);
				continue;
			}
			obj.Skeleton.SetToSetupPose();
			PreviewOf(obj)?.Apply(obj.Skeleton, time);
			obj.Skeleton.UpdatePose();
		}
	}

	private static Animation? PreviewOf(SceneObject obj) =>
		obj.FindAnimation(obj.PreviewAnimation) ?? obj.Animations.FirstOrDefault();

	// The canvas is in scene space here. Objects are drawn in list order (first = furthest back);
	// while you edit one, the others are faded and its bones and handles go on top of everything.
	private void DrawScene(SKCanvas canvas)
	{
		// The axes the panel's numbers are measured from: the open object's origin, or the scene's
		DrawInSpace(canvas, Editing ? _camera.Space : Matrix3x2.Identity, () => _renderer.DrawAxes(canvas));
		DrawOnionGhosts(canvas);

		foreach (var obj in _scene.Objects)
		{
			bool open = Editing && obj == _active;
			if (!obj.Visible && !open) continue;   // a hidden object still shows while you work on it
			DrawInSpace(canvas, _scene.WorldTransform(obj), () =>
			{
				if (Editing && !open) _renderer.DrawFaded(canvas, obj.Skeleton, FadedOpacity);
				else _renderer.DrawParts(canvas, obj.Skeleton);
				if (!Editing && _renderer.ImageBounds(obj.Skeleton) == null) _renderer.DrawBoneHints(canvas, obj.Skeleton);
			});
		}

		if (Editing)
			DrawInSpace(canvas, _camera.Space, () => _renderer.DrawOverlays(canvas, _skeleton));
		else if (_objectTool.Selected is { } selected && _scene.Objects.Contains(selected))
			ObjectGizmo.Draw(canvas, ObjectGizmo.For(_scene, selected, _renderer, _camera.Zoom), _camera.Zoom);
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

	// ======================= keys at scene level =======================

	// Only the keys for whole objects, playback and the view (true = handled)
	private bool HandleSceneKey(Key key)
	{
		switch (key)
		{
			case Key.Space: _state.Playing = !_state.Playing; break;
			case Key.R: _sceneTime = 0; break;
			case Key.Left: StepScene(-1); break;
			case Key.Right: StepScene(+1); break;
			case Key.Enter when _objectTool.Selected is { } obj: EnterObject(obj); break;
			case Key.Delete: DeleteCurrentObject(); break;
			case Key.Escape: _objectTool.Selected = null; break;
			case Key.F: _camera.Reset(); break;
			case Key.G: _renderer.ShowImages = !_renderer.ShowImages; break;
			case Key.P: _renderer.PixelArt = !_renderer.PixelArt; break;
			case Key.O: ToggleOnion(); break;
			default: return false;
		}
		return true;
	}

	private void StepScene(int frames)
	{
		_state.Playing = false;
		int frame = (int)MathF.Round(_sceneTime * _state.Fps) + frames;
		_sceneTime = Math.Max(0, frame) / (float)_state.Fps;
	}

	// ======================= Objects panel =======================

	private void RefreshScenePanel()
	{
		// An undo may have removed the open object, or the selected one
		if (_active == null || !_scene.Objects.Contains(_active)) EditObject(_scene.Objects[0]);
		if (_objectTool.Selected != null && !_scene.Objects.Contains(_objectTool.Selected)) _objectTool.Selected = null;

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
		AnimControls.IsEnabled = TimelineView.IsEnabled = !_sceneLevel;
		TimelineView.Opacity = _sceneLevel ? 0.35 : 1;

		SceneButton.Content = _sceneLevel ? "Edit ▸" : "◂ Scene (Esc)";
		SceneButton.IsEnabled = !_sceneLevel || _objectTool.Selected != null;
		ModeText.Text = _sceneLevel
			? "Scene: drag objects to place them. Double-click one (or Enter) to edit it."
			: $"Editing \"{_active?.Name}\". Other objects are faded. Esc = back to the scene.";

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
			Foreground = obj.Visible ? Brushes.White : Brushes.Gray,
			FontWeight = obj == _active && !_sceneLevel ? FontWeights.Bold : FontWeights.Normal
		};
	}

	// Called every frame: follows clicks in the viewport, and shows live values during drags
	private void SyncScenePanel()
	{
		if (!_sceneLevel) return;
		if (!Equals(_objectTool.Selected, _shownObject)) RefreshScenePanel();
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
		menu.IsOpen = true;
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
		_syncingScene = false;

		RefreshObjectValues();
	}

	private void RefreshObjectValues()
	{
		var obj = _objectTool.Selected;
		SetField(ObjectX, obj != null ? Num(obj.Position.X) : "");
		SetField(ObjectY, obj != null ? Num(obj.Position.Y) : "");
		SetField(ObjectRotation, obj != null ? Num(Deg(obj.Rotation)) : "");
		SetField(ObjectScaleX, obj != null ? Num(obj.Scale.X) : "");
		SetField(ObjectScaleY, obj != null ? Num(obj.Scale.Y) : "");
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
