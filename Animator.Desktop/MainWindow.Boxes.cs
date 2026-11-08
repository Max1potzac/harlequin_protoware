using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// Hitboxes and points, as the Windows editor has them (the Rig tab's Boxes view; J shows them
// without it): boxes and circles on bones for the game to test hits against, each of a kind
// (hurt, hit, block, trigger, or any word), on or off at rest and keyed on and off in animations;
// and points on bones (a muzzle, a hand to hold things), facing a way. With the view open they're
// picked and dragged in the viewport: the middle moves, a picked box's corner (a circle's edge)
// resizes. Sprite sheets export them per frame.
public sealed partial class MainWindow
{
	internal readonly ToggleButton BoxesView = new() { Content = "Boxes" };
	internal readonly ListBox BoxList = new() { MaxHeight = 160, MinHeight = 40 };
	internal readonly TextBox BoxName = new();
	internal readonly ComboBox BoxBoneChoice = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly TextBox BoxKind = new();
	internal readonly ComboBox BoxShape = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "Box", "Circle" } };
	internal readonly CheckBox BoxActive = new() { Content = "On at rest" };
	internal readonly Button BoxOnHere = new() { Content = "On from here", HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly Button BoxOffHere = new() { Content = "Off from here", HorizontalAlignment = HorizontalAlignment.Stretch };
	internal readonly Button BoxFit = new() { Content = "Fit round the picked part" };
	internal readonly Button BoxDelete = new() { Content = "Delete" };
	internal readonly TextBlock BoxHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
	internal readonly Dictionary<string, NumberField> BoxNumbers = new();
	private readonly Button _addHitbox = new() { Content = "+ Hitbox ▾" };
	private readonly Button _addPoint = new() { Content = "+ Point" };
	private readonly Button _boxKindMenu = new() { Content = "▾", Padding = new Thickness(8, 4) };
	private readonly StackPanel _boxSettings = new() { Spacing = 2 };
	private readonly StackPanel _boxHitboxOnly = new() { Spacing = 2 };
	private Grid _boxKeyRow = null!;
	private Control _boxesPanel = null!;
	private bool _boxesViewOn;
	private bool _showHitboxes;        // J: shown without the view
	private object? _boxItem;          // the Hitbox or RigPoint picked
	private object? _boxHover;         // ...and the one under the mouse
	private bool _syncingBoxes;
	private string? _boxesListed;      // what the list was made from
	private string? _boxNote;          // what an action had to say (until another one is picked)
	private (object?, bool) _boxNoteFor;

	internal bool ShowingBoxes => _boxesViewOn && !SceneLevel;
	internal bool ShowingHitboxes => _showHitboxes;
	internal object? PickedBox => _boxItem;
	internal object? HoveredBox => _boxHover;

	private void ToggleHitboxes()
	{
		_showHitboxes = !_showHitboxes;
		AnimHint(_showHitboxes ? "Hitboxes and points show (the Boxes view edits them)." : "");
	}

	private void BoxSay(string text)
	{
		_boxNote = text;
		_boxNoteFor = (_boxItem, State.AnimateMode);
		BoxHint.Text = text;
	}

	// ======================= the panel =======================

	private Control BuildBoxesPanel()
	{
		ToolTip.SetTip(BoxesView, "Hitboxes and points on bones, for the game: boxes and circles of a kind (hurt, hit, block…), keyed on and off in animations, and points (a muzzle, a hand). Drag them in the viewport while this view is open.");
		BoxesView.Click += (_, _) => { _boxesViewOn = true; _colourViewOn = false; RefreshRigList(); };
		ToolTip.SetTip(_addHitbox, "A box or circle on the picked bone (a part picked: on its bone, round it)");
		ToolTip.SetTip(_addPoint, "A point at the tip of the picked bone: where the game spawns or holds things");
		_addHitbox.Click += (_, _) => HitboxMenu().ShowAt(_addHitbox);
		_addPoint.Click += (_, _) => AddPoint();
		BoxDelete.Click += (_, _) =>
		{
			if (_boxItem is not { } item || !_session.DeleteBoxItem(item)) return;
			_boxItem = null;
			SyncBoxesPanel(force: true);
		};
		BoxList.SelectionChanged += (_, _) =>
		{
			if (_syncingBoxes || BoxList.SelectedItem is not ListBoxItem { Tag: { } item }) return;
			_boxItem = item;
			SyncBoxesPanel(force: true);
		};
		BoxName.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { CommitBoxName(); Viewport.Focus(); } };
		BoxName.LostFocus += (_, _) => CommitBoxName();
		ToolTip.SetTip(BoxBoneChoice, "The bone it moves with");
		BoxBoneChoice.SelectionChanged += (_, _) =>
		{
			if (_syncingBoxes || _boxItem is not { } item || BoxBoneChoice.SelectedItem is not string name || State.Skeleton.FindBone(name) is not { } bone) return;
			_session.SetBoxBone(item, bone);
			SyncBoxesPanel(force: true);
		};
		ToolTip.SetTip(BoxKind, "What it's for, as the game knows it: hurt (can be hit), hit (hits others), block, trigger… or any word");
		BoxKind.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { CommitBoxKind(BoxKind.Text); Viewport.Focus(); } };
		BoxKind.LostFocus += (_, _) => CommitBoxKind(BoxKind.Text);
		ToolTip.SetTip(_boxKindMenu, "The kinds there are");
		_boxKindMenu.Click += (_, _) =>
		{
			var menu = new MenuFlyout();
			foreach (var kind in Hitbox.CommonKinds.Concat(State.Skeleton.Hitboxes.Select(h => h.Kind)).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				var item = new MenuItem { Header = kind };
				item.Click += (_, _) => CommitBoxKind(kind);
				menu.Items.Add(item);
			}
			menu.ShowAt(_boxKindMenu);
		};
		BoxShape.SelectionChanged += (_, _) =>
		{
			if (_syncingBoxes || _boxItem is not Hitbox hitbox) return;
			_session.SetHitboxShape(hitbox, BoxShape.SelectedIndex == 1 ? HitboxShape.Circle : HitboxShape.Box);
			SyncBoxesPanel(force: true);
		};
		ToolTip.SetTip(BoxActive, "Whether it's on when an animation hasn't keyed it (an attack's hitbox: off, keyed on where it strikes)");
		BoxActive.IsCheckedChanged += (_, _) =>
		{
			if (_syncingBoxes || _boxItem is not Hitbox hitbox) return;
			_session.SetHitboxActive(hitbox, BoxActive.IsChecked == true);
			SyncBoxesPanel(force: true);
		};
		ToolTip.SetTip(BoxOnHere, "Keys it on at the playhead, until its next key");
		ToolTip.SetTip(BoxOffHere, "Keys it off at the playhead, until its next key");
		BoxOnHere.Click += (_, _) => KeyBox(true);
		BoxOffHere.Click += (_, _) => KeyBox(false);
		ToolTip.SetTip(BoxFit, "Round the part's visible pixels, as it rests (a box turns with the picture)");
		BoxFit.Click += (_, _) => FitBox();

		var kindRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
		Grid.SetColumn(_boxKindMenu, 1);
		_boxKindMenu.Margin = new Thickness(2, 0, 0, 0);
		kindRow.Children.Add(BoxKind);
		kindRow.Children.Add(_boxKindMenu);
		_boxKeyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 2) };
		BoxOnHere.Margin = new Thickness(0, 0, 2, 0);
		BoxOffHere.Margin = new Thickness(2, 0, 0, 0);
		Grid.SetColumn(BoxOffHere, 1);
		_boxKeyRow.Children.Add(BoxOnHere);
		_boxKeyRow.Children.Add(BoxOffHere);
		_boxHitboxOnly.Children.Add(Labelled("Kind", kindRow));
		_boxHitboxOnly.Children.Add(Labelled("Shape", BoxShape));
		_boxHitboxOnly.Children.Add(BoxActive);
		_boxHitboxOnly.Children.Add(_boxKeyRow);
		_boxHitboxOnly.Children.Add(BoxFit);

		_boxSettings.Children.Add(Labelled("Name", BoxName));
		_boxSettings.Children.Add(Labelled("Bone", BoxBoneChoice));
		_boxSettings.Children.Add(_boxHitboxOnly);
		foreach (var field in BuildBoxNumbers()) _boxSettings.Children.Add(field);

		var buttons = new WrapPanel();
		foreach (var b in new Control[] { _addHitbox, _addPoint, BoxDelete })
		{
			b.Margin = new Thickness(0, 0, 4, 4);
			buttons.Children.Add(b);
		}
		return new ScrollViewer
		{
			Content = new StackPanel
			{
				Margin = new Thickness(4),
				Spacing = 4,
				Children = { buttons, BoxList, _boxSettings, BoxHint }
			}
		};
	}

	// Its numbers, on its bone: where its middle is, its size, its angle; a point's facing
	private IEnumerable<NumberField> BuildBoxNumbers()
	{
		NumberField Number(string label, string tip, float step, Func<float?> read, Action<float> commit, float min = float.NegativeInfinity)
		{
			var field = new NumberField(label, step)
			{
				Read = read,
				Commit = commit,
				Min = min,
				Begin = () => State.Undo.BeginGroup(),   // a drag is one undo step
				End = () => State.Undo.EndGroup(),
				Problem = BoxSay
			};
			ToolTip.SetTip(field, tip);
			BoxNumbers[label] = field;
			return field;
		}
		static float Deg(float radians) => radians * 180f / MathF.PI;
		static float Rad(float degrees) => degrees * MathF.PI / 180f;
		yield return Number("X", "Its middle, along its bone", 1f,
			() => _boxItem switch { Hitbox h => h.Offset.X, RigPoint p => p.Offset.X, _ => null },
			v => _session.PlaceBoxItem(_boxItem!, offset: OffsetOf(_boxItem!) with { X = v }));
		yield return Number("Y", "Its middle, across its bone", 1f,
			() => _boxItem switch { Hitbox h => h.Offset.Y, RigPoint p => p.Offset.Y, _ => null },
			v => _session.PlaceBoxItem(_boxItem!, offset: OffsetOf(_boxItem!) with { Y = v }));
		yield return Number("Width", "Along its own sides", 1f,
			() => _boxItem is Hitbox { Shape: HitboxShape.Box } h ? h.Size.X : null,
			v => { if (_boxItem is Hitbox h) _session.PlaceBoxItem(h, size: h.Size with { X = v }); }, 1f);
		yield return Number("Height", "Along its own sides", 1f,
			() => _boxItem is Hitbox { Shape: HitboxShape.Box } h ? h.Size.Y : null,
			v => { if (_boxItem is Hitbox h) _session.PlaceBoxItem(h, size: h.Size with { Y = v }); }, 1f);
		yield return Number("Diameter", "Across the circle", 1f,
			() => _boxItem is Hitbox { Shape: HitboxShape.Circle } h ? h.Size.X : null,
			v => { if (_boxItem is Hitbox h) _session.PlaceBoxItem(h, size: new Vector2(v)); }, 1f);
		yield return Number("Angle °", "Degrees, from its bone's direction", 1f,
			() => _boxItem is Hitbox { Shape: HitboxShape.Box } h ? Deg(h.Rotation) : null,
			v => { if (_boxItem is Hitbox h) _session.PlaceBoxItem(h, rotation: Rad(v)); });
		yield return Number("Facing °", "Degrees, from its bone's direction (the way its line points)", 1f,
			() => _boxItem is RigPoint p ? Deg(p.Rotation) : null,
			v => { if (_boxItem is RigPoint p) _session.PlaceBoxItem(p, rotation: Rad(v)); });
	}

	private static Vector2 OffsetOf(object item) => item is Hitbox h ? h.Offset : ((RigPoint)item).Offset;

	private MenuFlyout HitboxMenu()
	{
		var menu = new MenuFlyout();
		foreach (var shape in new[] { HitboxShape.Box, HitboxShape.Circle })
			foreach (var kind in Hitbox.CommonKinds)
			{
				var item = new MenuItem { Header = $"{(shape == HitboxShape.Box ? "Box" : "Circle")}: {kind}" };
				item.Click += (_, _) => AddHitbox(shape, kind);
				menu.Items.Add(item);
			}
		return menu;
	}

	internal void AddHitbox(HitboxShape shape, string kind)
	{
		if (_session.BoxBone() is not { } bone)
		{
			BoxSay("Add a bone first: hitboxes go on bones.");
			return;
		}
		var made = _session.AddHitbox(shape, kind);
		if (made == null) return;
		_boxItem = made;
		SyncBoxesPanel(force: true);
		BoxSay($"{made.Name} is on {bone.Name}. Drag it in the viewport (a corner resizes), or type its numbers.");
	}

	internal void AddPoint()
	{
		if (_session.BoxBone() is not { } bone)
		{
			BoxSay("Add a bone first: points go on bones.");
			return;
		}
		var made = _session.AddPoint();
		if (made == null) return;
		_boxItem = made;
		SyncBoxesPanel(force: true);
		BoxSay($"{made.Name}: at the tip of {bone.Name}. Drag it, and type which way it faces.");
	}

	private void FitBox()
	{
		if (_boxItem is not Hitbox hitbox) return;
		if (State.SelectedSlot is not { } part)
		{
			BoxSay("Pick the part to fit it round (in the viewport): the Boxes view keeps its hitbox picked.");
			return;
		}
		bool fitted = _session.FitHitbox(hitbox, part);
		SyncBoxesPanel(force: true);
		if (!fitted) BoxSay($"{part.Name} shows nothing at rest to fit round.");
	}

	private void KeyBox(bool on)
	{
		if (_boxItem is not Hitbox hitbox) return;
		_session.KeyHitbox(hitbox, on);
		SyncBoxesPanel(force: true);
	}

	internal void CommitBoxName()
	{
		if (_syncingBoxes || _boxItem is not { } item) return;
		string name = BoxName.Text?.Trim() ?? "";
		string now = item is Hitbox h ? h.Name : ((RigPoint)item).Name;
		if (name.Length == 0 || name == now) { BoxName.Text = now; return; }
		bool taken = item is Hitbox ? State.Skeleton.FindHitbox(name) != null : State.Skeleton.FindPoint(name) != null;
		if (taken || !_session.RenameBoxItem(item, name))
		{
			BoxSay($"Another {(item is Hitbox ? "hitbox" : "point")} is called \"{name}\".");
			BoxName.Text = now;
			return;
		}
		SyncBoxesPanel(force: true);
	}

	internal void CommitBoxKind(string? kind)
	{
		if (_syncingBoxes || _boxItem is not Hitbox hitbox) return;
		if (!_session.SetHitboxKind(hitbox, kind ?? "")) BoxKind.Text = hitbox.Kind;
		SyncBoxesPanel(force: true);
	}

	// What the list shows, as text: made again when it changes (an edit, an undo, another object)
	private string BoxesKey()
	{
		var s = State.Skeleton;
		return string.Join("\n", s.Hitboxes.Select(h => $"{h.Name}|{h.Kind}|{h.Bone.Name}|{h.Shape}|{h.SetupActive}|{h.Active}"))
			+ "\n#" + string.Join("\n", s.Points.Select(p => $"{p.Name}|{p.Bone.Name}"))
			+ "\n#" + string.Join("|", s.AllBones().Select(b => b.Name))
			+ $"\n#{State.AnimateMode}|{(_boxItem is Hitbox hb ? s.Hitboxes.IndexOf(hb) : -1)}|{(_boxItem is RigPoint pt ? s.Points.IndexOf(pt) : -1)}";
	}

	// Every frame (and after an edit): the list, the picked one's settings and numbers
	private void SyncBoxesPanel(bool force = false)
	{
		if (!ShowingBoxes)
		{
			_boxHover = null;
			return;
		}
		var skeleton = State.Skeleton;
		if (_boxItem is Hitbox gone && !skeleton.Hitboxes.Contains(gone)) _boxItem = null;   // (another object, or undone)
		if (_boxItem is RigPoint lost && !skeleton.Points.Contains(lost)) _boxItem = null;
		if (_boxHover is Hitbox h0 && !skeleton.Hitboxes.Contains(h0) || _boxHover is RigPoint p0 && !skeleton.Points.Contains(p0)) _boxHover = null;

		string key = BoxesKey();
		if (force || key != _boxesListed)
		{
			_boxesListed = key;
			FillBoxList();
		}
		foreach (var field in BoxNumbers.Values)
		{
			field.Refresh(force);
			field.IsVisible = field.Read() != null;
		}

		if (_boxNote != null && _boxNoteFor != (_boxItem, State.AnimateMode)) _boxNote = null;
		BoxHint.Text = _boxNote ?? _boxItem switch
		{
			null when skeleton.Hitboxes.Count == 0 && skeleton.Points.Count == 0 =>
				"Pick a bone (or a part: the box goes round it), then + Hitbox or + Point. Hitboxes and points move with their bones; sprite sheets export them per frame.",
			null => "Click one in the viewport or the list. Drag to move it; a picked box's corner resizes it.",
			Hitbox h when State.AnimateMode => $"Animate: {h.Name} is {(h.Active ? "on" : "off")} at frame {State.Frame}. On / Off from here key it.",
			Hitbox => "Drag it in the viewport (a corner resizes, round its middle), or type its numbers. Positions are on its bone.",
			_ => "A point: where the game spawns or holds things, facing the way its line shows."
		};
	}

	private void FillBoxList()
	{
		var skeleton = State.Skeleton;
		_syncingBoxes = true;
		try
		{
			var items = new List<ListBoxItem>();
			foreach (var h in skeleton.Hitboxes)
			{
				var c = HitboxRenderer.ColorOf(h.Kind);
				string state = !h.SetupActive ? " · off at rest" : "";
				if (State.AnimateMode && h.Active != h.SetupActive) state = h.Active ? " · on here" : " · off here";
				items.Add(new ListBoxItem
				{
					Tag = h,
					Padding = new Thickness(6, 3),
					Content = new TextBlock { Text = $"{(h.Shape == HitboxShape.Box ? "■" : "●")} {h.Name}   {h.Kind} · {h.Bone.Name}{state}" },
					Foreground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue))
				});
			}
			foreach (var p in skeleton.Points)
				items.Add(new ListBoxItem { Tag = p, Padding = new Thickness(6, 3), Content = new TextBlock { Text = $"✛ {p.Name}   point · {p.Bone.Name}" } });
			BoxList.ItemsSource = items;
			BoxList.SelectedItem = items.FirstOrDefault(i => i.Tag == _boxItem);

			var hitbox = _boxItem as Hitbox;
			var point = _boxItem as RigPoint;
			_boxSettings.IsVisible = _boxItem != null;
			BoxDelete.IsEnabled = _boxItem != null;
			_boxHitboxOnly.IsVisible = hitbox != null;
			if (!BoxName.IsFocused) BoxName.Text = hitbox?.Name ?? point?.Name ?? "";
			var bones = skeleton.AllBones().Select(b => b.Name).ToList();
			if (BoxBoneChoice.ItemsSource is not List<string> listed || !listed.SequenceEqual(bones)) BoxBoneChoice.ItemsSource = bones;
			BoxBoneChoice.SelectedItem = hitbox?.Bone.Name ?? point?.Bone.Name;
			if (!BoxKind.IsFocused) BoxKind.Text = hitbox?.Kind ?? "";
			BoxShape.SelectedIndex = hitbox?.Shape == HitboxShape.Circle ? 1 : 0;
			BoxActive.IsChecked = hitbox?.SetupActive ?? true;
			_boxKeyRow.IsVisible = hitbox != null && State.AnimateMode;
		}
		finally
		{
			_syncingBoxes = false;
		}
	}

	// ======================= the viewport =======================

	// What's under the mouse: the picked box's handles first, then points, then the smallest box
	private (object Item, bool Resize)? BoxAt(Vector2 screen) =>
		_session.BoxItemAt(_camera.ScreenToWorld(screen), 8f / MathF.Max(_camera.Zoom, 1e-4f), _boxItem);

	// True: the mouse went down on a hitbox or point (the Boxes view open), and drags it
	private bool BoxMouseDown(Vector2 screen, PointerButton button)
	{
		if (!ShowingBoxes || button != PointerButton.Left || BoxAt(screen) is not { } hit) return false;
		_boxItem = hit.Item;
		_session.BeginBoxDrag(hit.Item, hit.Resize, _camera.ScreenToWorld(screen));
		SyncBoxesPanel(force: true);
		return true;
	}

	private void BoxHoverAt(Vector2 screen) => _boxHover = ShowingBoxes ? BoxAt(screen)?.Item : null;
}
