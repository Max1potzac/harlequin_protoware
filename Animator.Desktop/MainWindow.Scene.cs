using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The scene, as the Windows editor has it (Scene, SceneTimeline): at scene level the Rig tab
// lists the objects (front first) under + Object, Copy, Delete, ▲ / ▼ and Edit; the properties
// show the picked object's place, whether it shows, is locked, is exported, shows onion ghosts,
// the animation it plays when no clip does, and what carries it, and under that the picked clip
// (its animation, start, length, first frame, loop, fade in). The bar over the timeline is the
// scene's: its length, loop, stage and frame rate. A right-click on the scene timeline adds,
// changes and deletes clips. The edits are EditorSession's (EditorSession.Scene.cs).
public sealed partial class MainWindow
{
	// ======================= the objects (the top of the left panel) =======================

	private readonly Button _addObject = new() { Content = "+ New ▾" };
	internal readonly Button CopyObjectButton = new() { Content = "Copy" };
	internal readonly Button DeleteObjectButton = new() { Content = "Delete" };
	private readonly Button _objectUp = new() { Content = "▲" }, _objectDown = new() { Content = "▼" };
	internal readonly ListBox ObjectList = new() { MinHeight = 40, MaxHeight = 110 };
	internal readonly TextBlock ModeText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(2, 0, 2, 2) };
	private bool _syncingObjectList;

	// (the tests answer the blank drawn object's size and the picture this way)
	internal Func<NewPartWindow, Task<bool>>? AnswerNewDrawnObject { get; set; }

	private SceneObject? PickedObject => _session.SceneState.Selected;

	// The scene's objects, front first, in the scene and inside one alike: at scene level a click
	// picks one (a double-click opens it); inside one, a click opens another. Over it, the button
	// back to the scene (or into the picked object); under it, + New, Copy, Delete, ▲ / ▼.
	private Control BuildObjectsSection()
	{
		ToolTip.SetTip(_addObject, "Adds an object to the scene, in the middle of the view");
		ToolTip.SetTip(CopyObjectButton, "A copy of the object, with its own rig, animations and drawings");
		ToolTip.SetTip(DeleteObjectButton, "Deletes the object (undoable). A scene always keeps at least one.");
		ToolTip.SetTip(_objectUp, "Forward: in front of more objects");
		ToolTip.SetTip(_objectDown, "Backward: behind more objects");
		ToolTip.SetTip(ObjectList, "Top = in front. Double-click an object to edit it.");
		ToolTip.SetTip(_sceneButton, $"Back to the scene, to place whole objects ({_keys.Describe("edit.back")}). At scene level: open the picked object ({_keys.Describe("scene.open")})");
		_addObject.Click += (_, _) => AddObjectMenu().ShowAt(_addObject);
		CopyObjectButton.Click += (_, _) => CopyPickedObject();
		DeleteObjectButton.Click += (_, _) => DeletePickedObject();
		_objectUp.Click += (_, _) => { if (CurrentObject is { } o) _session.MoveObjectInOrder(o, +1); };
		_objectDown.Click += (_, _) => { if (CurrentObject is { } o) _session.MoveObjectInOrder(o, -1); };
		_sceneButton.Click += (_, _) =>
		{
			if (!SceneLevel) LeaveObject();
			else if (PickedObject is { } o) EnterObject(o);
		};
		ObjectList.SelectionChanged += (_, _) =>
		{
			if (_syncingObjectList || ObjectList.SelectedItem is not ListBoxItem { Tag: SceneObject obj }) return;
			if (SceneLevel) _session.SceneState.Selected = obj;
			else if (obj != _session.Active) Avalonia.Threading.Dispatcher.UIThread.Post(() => EnterObject(obj));   // (not in the middle of the list's own change)
		};
		ObjectList.DoubleTapped += (_, _) =>
		{
			if (ObjectList.SelectedItem is ListBoxItem { Tag: SceneObject obj }) Avalonia.Threading.Dispatcher.UIThread.Post(() => EnterObject(obj));
		};

		var header = new DockPanel { Margin = new Thickness(2, 0, 0, 4) };
		DockPanel.SetDock(_sceneButton, Dock.Right);
		header.Children.Add(_sceneButton);
		header.Children.Add(new TextBlock { Text = "Objects", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
		var buttons = new UniformGrid { Columns = 5, Margin = new Thickness(0, 4, 0, 4) };
		foreach (var b in new[] { _addObject, CopyObjectButton, DeleteObjectButton, _objectUp, _objectDown })
		{
			b.Margin = new Thickness(0, 0, 3, 0);
			b.Padding = new Thickness(2, 4);
			b.HorizontalAlignment = HorizontalAlignment.Stretch;
			b.HorizontalContentAlignment = HorizontalAlignment.Center;
			buttons.Children.Add(b);
		}
		return new StackPanel
		{
			Margin = new Thickness(6, 6, 6, 0),
			Children = { header, ObjectList, buttons, ModeText, new Border { Height = 1, Background = Brushes.Gray, Opacity = 0.5, Margin = new Thickness(0, 4, 0, 4) } }
		};
	}

	// The object the buttons act on: the picked one at scene level, the open one inside
	private SceneObject? CurrentObject => SceneLevel ? PickedObject : _session.Active;

	private MenuFlyout AddObjectMenu()
	{
		var menu = new MenuFlyout();
		void Add(string header, Action click)
		{
			var item = new MenuItem { Header = header };
			item.Click += (_, _) => click();
			menu.Items.Add(item);
		}
		Add("Rigged object (bones)", AddRiggedObject);
		Add("Drawn object (blank)…", () => _ = AddBlankDrawnObject());
		Add("Drawn object from a picture…", () => _ = AddDrawnObjectFromPicture());
		Add("From a file (Photoshop, Animate, sprite sheet)…", () => _ = ImportFile());
		return menu;
	}

	// A new rig to build: it opens, in the middle of the view
	internal void AddRiggedObject()
	{
		State.Playing = false;
		EnterObject(_session.AddRiggedObject(_camera.Pan));
	}

	internal async Task AddBlankDrawnObject()
	{
		var dialog = new NewPartWindow("New drawn object", "drawing", 256, 256,
			"A drawn object has no bones: just parts you paint, frame by frame if you like. It starts with one empty part this size.");
		bool ok = AnswerNewDrawnObject is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (ok) AddDrawnObject(dialog.PartName, null, dialog.PartWidth, dialog.PartHeight, paint: true);
	}

	internal async Task AddDrawnObjectFromPicture()
	{
		string? file = AnswerPartPicture is { } answer ? await answer() : await PickPicture();
		if (file == null) return;
		using var bitmap = SKBitmap.Decode(file);
		if (bitmap == null || bitmap.Width > 4096 || bitmap.Height > 4096)
		{
			await Dialogs.Tell(this, "New drawn object", bitmap == null ? "That file couldn't be read as a picture." : "Pictures up to 4096 × 4096 can be drawn objects.");
			return;
		}
		AddDrawnObject(Path.GetFileNameWithoutExtension(file), bitmap, bitmap.Width, bitmap.Height, paint: false);
	}

	// A drawn object with one part, in the middle of the view, opened with that part picked (a
	// blank one in the paint editor)
	private void AddDrawnObject(string name, SKBitmap? pixels, int width, int height, bool paint)
	{
		var (created, part, _) = _session.AddDrawnObject(name, pixels, width, height, _camera.Pan);
		State.Playing = false;
		EnterObject(created);
		State.Selected = part.Bone;
		State.SelectedSlot = part;
		RefreshRigList();
		if (paint) OpenPaintEditorOn(part);
	}

	internal void CopyPickedObject()
	{
		if (CurrentObject is not { } source) return;
		_session.FinishPaintTool();   // a half-placed move or figure lands first, so the copy has it
		var copy = _session.CopyObject(source);
		if (SceneLevel) _session.SceneState.Selected = copy;
		else EnterObject(copy);
	}

	internal void DeletePickedObject()
	{
		if (CurrentObject is not { } obj) return;
		if (Scene.Objects.Count < 2)
		{
			Hint("A scene keeps at least one object.");
			return;
		}
		bool open = obj == _session.Active && !SceneLevel;
		if (open) ClosePaintEditor();   // (its drawing goes with it)
		_session.DeleteObject(obj);
		if (open) EnterObject(_session.Active!);   // (the window's side of the one the session opened instead)
	}

	// One row of the list: its name, kind, and whether it's hidden or locked
	private static string ObjectRowText(SceneObject obj) =>
		$"{obj.Name}   ({(obj.Kind == ObjectKind.Drawn ? "drawn" : "rig")}{(obj.Visible ? "" : ", hidden")}{(obj.Locked ? ", locked" : "")})";

	private void SyncSceneTools()
	{
		bool any = CurrentObject != null;
		CopyObjectButton.IsEnabled = _objectUp.IsEnabled = _objectDown.IsEnabled = any;
		DeleteObjectButton.IsEnabled = any && Scene.Objects.Count > 1;
	}

	// ======================= the picked object's settings =======================

	private readonly TextBlock _objectInfo = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
	internal readonly CheckBox ObjectVisible = new() { Content = "Visible" };
	internal readonly CheckBox ObjectLocked = new() { Content = "Locked" };
	internal readonly CheckBox ObjectExport = new() { Content = "Export" };
	internal readonly CheckBox ObjectOnion = new() { Content = "Onion skin" };
	internal readonly ComboBox ObjectPreview = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly ComboBox ObjectAttach = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly Button _addClip = new() { Content = "+ Clip at playhead ▾" };
	private bool _syncingScene;
	private string? _sceneShown;   // what the object's and clip's settings were filled in from

	// The picked clip
	private readonly StackPanel _clipBox = new() { Spacing = 2, Margin = new Thickness(0, 10, 0, 0) };
	private readonly TextBlock _clipTitle = new() { FontWeight = FontWeight.SemiBold };
	internal readonly ComboBox ClipAnimation = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly CheckBox ClipLoop = new() { Content = "Loop" };
	internal readonly Dictionary<string, NumberField> ClipNumbers = new();
	private readonly TextBlock _clipHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, FontSize = 12 };

	private Control BuildObjectSettings()
	{
		ToolTip.SetTip(ObjectVisible, "Hidden objects aren't drawn in the scene (they still show while you edit them)");
		ToolTip.SetTip(ObjectLocked, "Locked objects can't be clicked or dragged in the viewport (handy for backgrounds)");
		ToolTip.SetTip(ObjectExport, "Included when exporting");
		ToolTip.SetTip(ObjectOnion, "Shows onion-skin ghosts at scene level (O turns onion skin on and off)");
		ToolTip.SetTip(ObjectPreview, "The animation this object plays at scene level when no clip of it does, and while you edit other objects");
		ToolTip.SetTip(ObjectAttach, "Carried by another object's bone (a sword in a hand, a hat on a head): it moves, turns and scales with that bone. It stays where it is when you attach or free it.");
		ToolTip.SetTip(_addClip, "Puts one of its animations on the scene timeline, starting at the playhead (or right-click its row in the scene timeline)");
		foreach (var box in new[] { ObjectVisible, ObjectLocked, ObjectExport, ObjectOnion })
			box.IsCheckedChanged += (_, _) =>
			{
				if (_syncingScene || PickedObject is not { } obj) return;
				_session.SetObjectFlags(obj, ObjectVisible.IsChecked == true, ObjectLocked.IsChecked == true, ObjectExport.IsChecked == true, ObjectOnion.IsChecked == true);
			};
		ObjectPreview.SelectionChanged += (_, _) =>
		{
			if (!_syncingScene && PickedObject is { } obj && ObjectPreview.SelectedItem is string name) _session.SetPreview(obj, name);
		};
		ObjectAttach.SelectionChanged += (_, _) =>
		{
			if (_syncingScene || PickedObject is not { } obj || ObjectAttach.SelectedItem is not AttachChoice choice) return;
			if (!_session.AttachObject(obj, choice)) SyncObjectSettings(force: true);   // (the choice back to what's really there)
		};
		_addClip.Click += (_, _) =>
		{
			if (PickedObject is not { } obj) return;
			int frame = _session.ToFrames(_session.SceneState.Time);
			var menu = new MenuFlyout();
			foreach (var animation in obj.Animations)
			{
				var a = animation;
				var item = new MenuItem { Header = a.Name };
				item.Click += (_, _) => AddClip(obj, a, frame);
				menu.Items.Add(item);
			}
			menu.ShowAt(_addClip);
		};
		var edit = new Button { Content = "Edit this object ▸" };
		ToolTip.SetTip(edit, "Opens the object to edit its rig, drawings and animations (double-click it, or Enter)");
		edit.Click += (_, _) => { if (PickedObject is { } o) EnterObject(o); };

		// The clip
		ToolTip.SetTip(ClipAnimation, "Which of the object's animations this clip plays");
		ToolTip.SetTip(ClipLoop, "On: a clip longer than its animation plays it again. Off: it holds the last frame.");
		ClipAnimation.SelectionChanged += (_, _) =>
		{
			if (!_syncingScene && _session.SceneState.SelectedClip is { } clip && ClipAnimation.SelectedItem is AnimationChoice { Animation: var a }) _session.SetClipAnimation(clip, a);
		};
		ClipLoop.IsCheckedChanged += (_, _) =>
		{
			if (!_syncingScene && _session.SceneState.SelectedClip is { } clip) _session.SetClipLoop(clip, ClipLoop.IsChecked == true);
		};
		NumberField ClipNumber(string label, string tip, float min, Func<SceneClip, float> read, Action<SceneObject, SceneClip, int> commit)
		{
			var field = new NumberField(label)
			{
				Whole = true,
				Min = min,
				Read = () => ShownClip() is { } c ? _session.ToFrames(read(c.Clip)) : null,
				Commit = v =>
				{
					if (ShownClip() is not { } c) return;
					_clipHint.Text = "";
					commit(c.Object, c.Clip, (int)v);
				},
				Begin = () => State.Undo.BeginGroup(),   // a drag is one undo step
				End = () => State.Undo.EndGroup(),
				Problem = text => _clipHint.Text = text
			};
			ToolTip.SetTip(field, tip);
			ClipNumbers[label] = field;
			return field;
		}
		void Limited(string what, int asked, int got)
		{
			if (asked != got) _clipHint.Text = $"Its {what} only goes as far as the clips next to it allow.";
		}
		var start = ClipNumber("Start", "The scene frame the clip starts on", 0, c => c.Start, (o, c, v) =>
		{
			_session.SetClipTiming(o, c, start: v);
			Limited("start", v, _session.ToFrames(c.Start));
		});
		var length = ClipNumber("Length", "How many scene frames it lasts. Longer than the animation: it loops, or holds its last frame.", 1, c => c.Length, (o, c, v) =>
		{
			_session.SetClipTiming(o, c, length: v);
			Limited("length", v, _session.ToFrames(c.Length));
		});
		var from = ClipNumber("From frame", "The animation's frame the clip starts on (the frames before it are trimmed off)", 0, c => c.Offset,
			(o, c, v) => _session.SetClipTiming(o, c, offset: v));
		var fade = ClipNumber("Fade in", "Frames: the clip fades in from the clip before it (its animation going on) instead of cutting to it. 0 = a cut.", 0, c => c.MixIn,
			(o, c, v) => _session.SetClipTiming(o, c, mixIn: v));
		var asMixed = new Button { Content = "Fade as mixed" };
		ToolTip.SetTip(asMixed, "The fade the mixer has for these two animations (from the clip before's animation to this one's)");
		asMixed.Click += (_, _) =>
		{
			if (ShownClip() is not { } c) return;
			if (c.Object.ClipBefore(c.Clip) == null) _clipHint.Text = "No clip before it to fade in from.";
			else _session.MixClipFromPlan(c.Object, c.Clip);
		};
		var editClip = new Button { Content = "Edit in place ▸" };
		ToolTip.SetTip(editClip, "Opens the object at this clip's animation and frame; the rest of the scene plays along (or double-click the clip)");
		editClip.Click += (_, _) => { if (ShownClip() is { } c) EnterObject(c.Object, c.Clip); };
		var deleteClip = new Button { Content = "Delete clip" };
		ToolTip.SetTip(deleteClip, _keys.Describe("edit.delete"));
		deleteClip.Click += (_, _) => DeletePickedClip();
		_clipBox.Children.Add(_clipTitle);
		_clipBox.Children.Add(Labelled("Animation", ClipAnimation));
		foreach (var n in new[] { start, length, from }) _clipBox.Children.Add(n);
		_clipBox.Children.Add(ClipLoop);
		_clipBox.Children.Add(fade);
		_clipBox.Children.Add(new WrapPanel { Children = { Spaced(asMixed), Spaced(editClip), Spaced(deleteClip) } });
		_clipBox.Children.Add(_clipHint);

		var flags = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
		Grid.SetColumn(ObjectLocked, 1);
		Grid.SetRow(ObjectExport, 1);
		Grid.SetRow(ObjectOnion, 1);
		Grid.SetColumn(ObjectOnion, 1);
		flags.Children.Add(ObjectVisible);
		flags.Children.Add(ObjectLocked);
		flags.Children.Add(ObjectExport);
		flags.Children.Add(ObjectOnion);
		return new StackPanel
		{
			Spacing = 2,
			Children =
			{
				flags,
				Labelled("Preview", ObjectPreview),
				Labelled("Carried by", ObjectAttach),
				new WrapPanel { Margin = new Thickness(0, 4, 0, 0), Children = { Spaced(edit), Spaced(_addClip) } },
				_objectInfo,
				_clipBox
			}
		};
	}

	private static Control Spaced(Control c)
	{
		c.Margin = new Thickness(0, 0, 4, 4);
		return c;
	}

	// The picked clip, when it's the picked object's
	private (SceneObject Object, SceneClip Clip)? ShownClip() =>
		PickedObject is { } obj && _session.SceneState.SelectedClip is { } clip && obj.Clips.Contains(clip) ? (obj, clip) : null;

	// Every frame at scene level: filled in again when what it shows changes (another object or
	// clip, an undo), its numbers current
	private void SyncObjectSettings(bool force = false)
	{
		if (PickedObject is not { } obj) return;
		var clip = ShownClip()?.Clip;
		string key = $"{RuntimeHelpers.GetHashCode(obj)}|{obj.Name}|{obj.Visible}{obj.Locked}{obj.Export}{obj.Onion}|{ScenePlayer.Preview(obj)?.Name}" +
			$"|{obj.AttachObject}/{obj.AttachBone}|{string.Join(",", obj.Animations.Select(a => a.Name))}|{obj.Skeleton.Slots.Count}" +
			$"|{(clip == null ? 0 : RuntimeHelpers.GetHashCode(clip))}|{clip?.Animation.Name}|{clip?.Loop}|{string.Join(",", Scene.Objects.Select(o => o.Name))}";
		if (force || key != _sceneShown)
		{
			_sceneShown = key;
			FillObjectSettings(obj, clip);
		}
		foreach (var n in ClipNumbers.Values) n.Refresh(force);
	}

	private void FillObjectSettings(SceneObject obj, SceneClip? clip)
	{
		_syncingScene = true;
		try
		{
			ObjectVisible.IsChecked = obj.Visible;
			ObjectLocked.IsChecked = obj.Locked;
			ObjectExport.IsChecked = obj.Export;
			ObjectOnion.IsChecked = obj.Onion;
			ObjectPreview.ItemsSource = obj.Animations.Select(a => a.Name).ToList();
			ObjectPreview.SelectedItem = ScenePlayer.Preview(obj)?.Name;
			var attach = _session.AttachChoices(obj);
			string? holder = Scene.AttachmentBone(obj) != null ? obj.AttachObject : null;
			string? bone = holder != null ? obj.AttachBone : null;
			var carrier = attach.FirstOrDefault(c => holder != null && c.Holder?.Name == holder && c.Bone == bone);
			ObjectAttach.ItemsSource = attach;
			ObjectAttach.SelectedItem = carrier ?? attach[0];
			_objectInfo.Text = $"{(obj.Kind == ObjectKind.Drawn ? "Drawn object" : "Rigged object")}: {obj.Skeleton.Slots.Count} part(s), {obj.Animations.Count} animation(s)"
				+ (carrier != null ? $". Carried by {carrier.Label}: X / Y, rotation and scale are measured from it." : "");
			_addClip.IsEnabled = obj.Animations.Count > 0;

			_clipBox.IsVisible = clip != null;
			if (clip != null)
			{
				_clipTitle.Text = $"Clip of {obj.Name} on the scene timeline";
				var choices = obj.Animations.Select(a => new AnimationChoice(a)).ToList();
				ClipAnimation.ItemsSource = choices;
				ClipAnimation.SelectedItem = choices.FirstOrDefault(c => c.Animation == clip.Animation);
				ClipLoop.IsChecked = clip.Loop;
			}
			else _clipHint.Text = "";
		}
		finally
		{
			_syncingScene = false;
		}
	}

	// ======================= clips =======================

	// A clip of an animation on the object's track at a scene frame, one full play long
	internal SceneClip? AddClip(SceneObject obj, Animation animation, int frame)
	{
		var made = _session.AddClip(obj, animation, frame);
		if (made == null) SceneHint("No room there: the next clip starts right away.");
		else
		{
			_session.SceneState.Selected = obj;
			_session.SceneState.SelectedClip = made;
		}
		return made;
	}

	internal void DeletePickedClip()
	{
		if (ShownClip() is { } c) _session.DeleteClip(c.Object, c.Clip);
	}

	// Delete at scene level: the picked clip, or else the picked object
	private void DeleteAtSceneLevel()
	{
		if (ShownClip() != null) DeletePickedClip();
		else DeletePickedObject();
	}

	// A right-click in the scene timeline: on a clip, its options; anywhere on a row, adding one
	private void ShowClipMenu(SceneObject obj, SceneClip? clip, int frame) => ClipMenu(obj, clip, frame).Open(TimelineView);

	internal ContextMenu ClipMenu(SceneObject obj, SceneClip? clip, int frame)
	{
		var menu = new ContextMenu();
		MenuItem Item(string header, Action click, string? tip = null)
		{
			var item = new MenuItem { Header = header };
			if (tip != null) ToolTip.SetTip(item, tip);
			item.Click += (_, _) => click();
			return item;
		}
		if (clip != null)
		{
			var animations = new MenuItem { Header = "Animation" };
			foreach (var animation in obj.Animations)
			{
				var a = animation;
				var item = new MenuItem { Header = a.Name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = a == clip.Animation };
				item.Click += (_, _) => _session.SetClipAnimation(clip, a);
				animations.Items.Add(item);
			}
			menu.Items.Add(animations);
			var loop = new MenuItem { Header = "Loop", ToggleType = MenuItemToggleType.CheckBox, IsChecked = clip.Loop };
			ToolTip.SetTip(loop, "Off: after its last frame, the animation holds it");
			loop.Click += (_, _) => _session.SetClipLoop(clip, !clip.Loop);
			menu.Items.Add(loop);
			menu.Items.Add(Item("One play long", () => _session.FitClip(obj, clip)));
			menu.Items.Add(Item("Edit in place ▸", () => EnterObject(obj, clip)));
			menu.Items.Add(Item("Delete clip", () => _session.DeleteClip(obj, clip)));
			menu.Items.Add(new Separator());
		}
		var add = new MenuItem { Header = $"Add a clip at frame {frame}", IsEnabled = obj.Animations.Count > 0 };
		foreach (var animation in obj.Animations)
		{
			var a = animation;
			add.Items.Add(Item(a.Name, () => AddClip(obj, a, frame)));
		}
		menu.Items.Add(add);
		if (frame > 0) menu.Items.Add(Item($"End the scene at frame {frame}", () => _session.SetSceneLength(frame)));
		return menu;
	}

	// ======================= the scene's bar (over the scene timeline) =======================

	internal readonly TextBox SceneLength = new() { Width = 64 };
	internal readonly CheckBox SceneLoop = new() { Content = "Loop" };
	internal readonly TextBox StageWidth = new() { Width = 64 };
	internal readonly TextBox StageHeight = new() { Width = 64 };
	private readonly TextBlock _sceneHint = new() { Foreground = Brushes.Orange, VerticalAlignment = VerticalAlignment.Center };

	private void SceneHint(string text) => _sceneHint.Text = text;

	// The bar's scene section (see BuildTimelineBar): the scene's length, loop and stage
	private List<Control> BuildSceneControls()
	{
		_sceneTimeline.MenuRequested += ShowClipMenu;
		static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
		Control Tip(Control c, string tip)
		{
			ToolTip.SetTip(c, tip);
			return c;
		}
		var fit = new Button { Content = "Fit to clips" };
		fit.Click += (_, _) =>
		{
			if (SceneOps.LastClipEnd(Scene) <= 0) SceneHint("There are no clips yet: right-click an object's row in the scene timeline to add one.");
			else _session.FitSceneToClips();
		};
		SceneLoop.IsCheckedChanged += (_, _) => { if (!_syncingScene) _session.SetSceneLoop(SceneLoop.IsChecked == true); };
		foreach (var box in new[] { SceneLength, StageWidth, StageHeight })
		{
			var b = box;
			b.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { CommitSceneField(b); TimelineView.Focus(); } };
			b.LostFocus += (_, _) => CommitSceneField(b);
		}
		_sceneSection = new List<Control>
		{
			new TextBlock { Text = "Scene", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold },
			Label("Length (frames)"),
			Tip(SceneLength, "How long the scene timeline runs"),
			Tip(fit, "Makes the scene end where its last clip ends"),
			Tip(SceneLoop, "Plays the scene again from the start when it ends"),
			Tip(Label("Stage"), "The picture the scene is made for (its video size), centred on the scene's origin. 0 × 0: none."),
			Tip(StageWidth, "Stage width (0: no stage)"),
			new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center },
			Tip(StageHeight, "Stage height (0: no stage)")
		};
		return _sceneSection;
	}

	private List<Control> _sceneSection = new();

	internal void CommitSceneField(TextBox box)
	{
		if (_syncingScene || !SceneLevel) return;
		SceneHint("");
		string text = box.Text?.Trim() ?? "";
		int now = SceneFieldValue(box);
		if (text == now.ToString()) return;
		(int min, int max, string problem) = box == SceneLength ? (1, 100000, "The scene's length is a whole number of frames (1 or more).")
			: (0, 100000, "The stage size is a whole number of pixels (0: no stage).");
		if (!int.TryParse(text, out int v) || v < min || v > max)
		{
			SceneHint(problem);
			box.Text = now.ToString();
			return;
		}
		if (box == SceneLength) _session.SetSceneLength(v);
		else if (box == StageWidth) _session.SetStage(v, Scene.StageHeight);
		else _session.SetStage(Scene.StageWidth, v);
	}

	private int SceneFieldValue(TextBox box) =>
		box == SceneLength ? _session.ToFrames(Scene.Duration) : box == StageWidth ? Scene.StageWidth : Scene.StageHeight;

	// Every frame: the bar shows the scene's (after an edit, an undo, another file)
	private void SyncScene()
	{
		foreach (var c in _sceneSection) c.IsVisible = SceneLevel;
		SyncObjectList();
		if (!SceneLevel) return;
		_syncingScene = true;
		try
		{
			if (!AnimFps.IsFocused && AnimFps.Text != State.Fps.ToString()) AnimFps.Text = State.Fps.ToString();
			foreach (var box in new[] { SceneLength, StageWidth, StageHeight })
				if (!box.IsFocused && box.Text != SceneFieldValue(box).ToString()) box.Text = SceneFieldValue(box).ToString();
			SceneLoop.IsChecked = Scene.Loop;
		}
		finally
		{
			_syncingScene = false;
		}
	}

	// The objects' rows follow renames, flags, the order and which is open (an undo, the buttons);
	// the picked (or open) one is selected
	private string? _objectsListed;
	private List<ListBoxItem> _objectRows = new();

	private void SyncObjectList()
	{
		var open = SceneLevel ? null : _session.Active;
		string key = string.Join("\n", Scene.Objects.Select(o => RuntimeHelpers.GetHashCode(o) + ObjectRowText(o))) + $"|{(open == null ? 0 : RuntimeHelpers.GetHashCode(open))}";
		_syncingObjectList = true;
		try
		{
			if (key != _objectsListed)
			{
				_objectsListed = key;
				_objectRows = Scene.Objects.AsEnumerable().Reverse()   // (front first, like layers)
					.Select(o => new ListBoxItem
					{
						Tag = o,
						Padding = new Thickness(8, 3),
						Content = new TextBlock { Text = ObjectRowText(o), FontWeight = o == open ? FontWeight.Bold : FontWeight.Normal, Opacity = o.Visible ? 1 : 0.55 }
					}).ToList();
				ObjectList.ItemsSource = _objectRows;
			}
			var current = SceneLevel ? PickedObject : _session.Active;
			var row = _objectRows.FirstOrDefault(r => r.Tag == current);
			if (ObjectList.SelectedItem != row) ObjectList.SelectedItem = row;
		}
		finally
		{
			_syncingObjectList = false;
		}
		SyncSceneTools();
		ObjectNone.IsVisible = SceneLevel && PickedObject == null;
	}
}
