using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SkiaSharp;

namespace Animator.Viewer;

// Hitboxes and points (the rig panel's Boxes view; J shows them without it): boxes and circles on
// bones for the game to test hits against, each of a kind (hurt, hit, block, trigger, or any),
// on or off at rest and keyed on and off in animations; and points on bones (a muzzle, a hand to
// hold things), facing a way. With the view open, they're picked and dragged in the viewport: the
// middle moves, a picked box's corner (a circle's edge) resizes. Sprite sheets export them per frame.
public partial class MainWindow
{
	private bool _boxesView;
	private bool _showHitboxes;
	private object? _boxItem;    // the Hitbox or RigPoint selected
	private object? _boxHover;
	private bool _syncingBoxes;

	private bool _boxDrag => _session.DraggingBox;   // (see EditorSession.BeginBoxDrag)


	private void ToggleHitboxes()
	{
		_showHitboxes = !_showHitboxes;
		AnimHint.Text = _showHitboxes ? "Hitboxes and points show (the Boxes view edits them)." : "";
	}

	private void EditBoxes(string name, Func<bool> change) => _session.EditBoxes(name, change);

	// ======================= adding and deleting =======================

	private Bone? BoxBone() => _session.BoxBone();

	private void BoxAdd_Click(object sender, RoutedEventArgs e)
	{
		if (_sceneLevel) return;
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		foreach (var shape in new[] { HitboxShape.Box, HitboxShape.Circle })
			foreach (var kind in Hitbox.CommonKinds)
			{
				var item = new MenuItem { Header = $"{(shape == HitboxShape.Box ? "Box" : "Circle")}: {kind}" };
				item.Click += (_, _) => AddHitbox(shape, kind);
				menu.Items.Add(item);
			}
		menu.IsOpen = true;
	}

	private void AddHitbox(HitboxShape shape, string kind)
	{
		if (BoxBone() is not { } bone)
		{
			BoxHint.Text = "Add a bone first: hitboxes go on bones.";
			return;
		}
		var made = _session.AddHitbox(shape, kind);
		_boxItem = made;
		BuildBoxesPanel();
		BoxHint.Text = $"{made?.Name} is on {bone.Name}. Drag it in the viewport (a corner resizes), or type its numbers.";
	}

	private void PointAdd_Click(object sender, RoutedEventArgs e)
	{
		if (_sceneLevel) return;
		if (BoxBone() is not { } bone)
		{
			BoxHint.Text = "Add a bone first: points go on bones.";
			return;
		}
		var made = _session.AddPoint();
		_boxItem = made;
		BuildBoxesPanel();
		BoxHint.Text = $"{made?.Name}: at the tip of {bone.Name}. Drag it, and type which way it faces.";
	}

	private void BoxDelete_Click(object sender, RoutedEventArgs e)
	{
		if (_boxItem is not { } item || !_session.DeleteBoxItem(item)) return;
		_boxItem = null;
		BuildBoxesPanel();
	}

	// Round the picked part's visible pixels (as it rests)
	private void BoxFit_Click(object sender, RoutedEventArgs e)
	{
		if (_boxItem is not Hitbox hitbox) return;
		if (_state.SelectedSlot is not { } part)
		{
			BoxHint.Text = "Pick the part to fit it round (in the viewport, or the Bones or Order view): the Boxes view keeps its hitbox selected.";
			return;
		}
		bool fitted = _session.FitHitbox(hitbox, part);
		BuildBoxesPanel();
		if (!fitted) BoxHint.Text = $"{part.Name} shows nothing at rest to fit round.";
	}

	// (see EditorSession.VisibleRect)
	private static (float X, float Y, float W, float H)? VisibleRect(SKBitmap bitmap) => EditorSession.VisibleRect(bitmap);

	// Animate mode: on or off from the playhead on
	private void BoxOnHere_Click(object sender, RoutedEventArgs e) => KeyBox(true);
	private void BoxOffHere_Click(object sender, RoutedEventArgs e) => KeyBox(false);

	private void KeyBox(bool on)
	{
		if (_boxItem is not Hitbox hitbox) return;
		_session.KeyHitbox(hitbox, on);
		BuildBoxesPanel();
	}

	// ======================= the viewport =======================

	// What's under the mouse: the picked box's handles first, then points, then the smallest box
	private (object Item, bool Resize)? BoxAt(Vector2 screen) =>
		_session.BoxItemAt(_camera.ScreenToWorld(screen), 8f / _camera.Zoom, _boxItem);

	// True: the mouse went down on a hitbox or point (the Boxes view open), and drags it
	private bool BoxMouseDown(Vector2 screen, PointerButton button)
	{
		if (!_boxesView || _sceneLevel || button != PointerButton.Left || _tool.Tool == ViewTool.Paint) return false;
		if (BoxAt(screen) is not { } hit) return false;
		_boxItem = hit.Item;
		_session.BeginBoxDrag(hit.Item, hit.Resize, _camera.ScreenToWorld(screen));
		BuildBoxesPanel();
		return true;
	}

	private void BoxMouseMove(Vector2 screen) => _session.DragBoxTo(_camera.ScreenToWorld(screen));

	private void BoxMouseUp()
	{
		_session.EndBoxDrag();
		BuildBoxesPanel();
	}

	private void BoxHoverAt(Vector2 screen) =>
		_boxHover = _boxesView && !_sceneLevel ? BoxAt(screen)?.Item : null;

	// ======================= the panel =======================

	private void BoxList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingBoxes || BoxList.SelectedItem is not ListBoxItem { Tag: { } item }) return;
		_boxItem = item;
		BuildBoxesPanel();
	}

	private void BoxName_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitBoxName();
		Viewport.Focus();
	}

	private void BoxName_LostFocus(object sender, RoutedEventArgs e) => CommitBoxName();

	private void CommitBoxName()
	{
		if (_syncingBoxes || _boxItem == null) return;
		string name = BoxName.Text.Trim();
		switch (_boxItem)
		{
			case Hitbox hitbox when name.Length > 0 && name != hitbox.Name:
				if (_skeleton.FindHitbox(name) != null) { BoxHint.Text = $"Another hitbox is called \"{name}\"."; BoxName.Text = hitbox.Name; return; }
				_session.RenameBoxItem(hitbox, name);
				break;
			case RigPoint point when name.Length > 0 && name != point.Name:
				if (_skeleton.FindPoint(name) != null) { BoxHint.Text = $"Another point is called \"{name}\"."; BoxName.Text = point.Name; return; }
				_session.RenameBoxItem(point, name);
				break;
			default: return;
		}
		BuildBoxesPanel();
	}

	private void BoxKind_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitBoxKind();
		Viewport.Focus();
	}

	private void BoxKind_LostFocus(object sender, RoutedEventArgs e) => CommitBoxKind();

	private void BoxKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingBoxes || BoxKind.SelectedItem is not string chosen) return;
		BoxKind.Text = chosen;
		CommitBoxKind();
	}

	private void CommitBoxKind()
	{
		if (_syncingBoxes || _boxItem is not Hitbox hitbox) return;
		if (_session.SetHitboxKind(hitbox, BoxKind.Text)) BuildBoxesPanel();
	}

	private void BoxShape_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingBoxes || _boxItem is not Hitbox hitbox) return;
		var shape = BoxShape.SelectedIndex == 1 ? HitboxShape.Circle : HitboxShape.Box;
		if (_session.SetHitboxShape(hitbox, shape)) BuildBoxesPanel();
	}

	private void BoxBone_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingBoxes || BoxBoneChoice.SelectedItem is not string boneName || _skeleton.FindBone(boneName) is not { } bone) return;
		if (_boxItem is { } item) _session.SetBoxBone(item, bone);
		BuildBoxesPanel();
	}

	private void BoxActive_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingBoxes || _boxItem is not Hitbox hitbox) return;
		_session.SetHitboxActive(hitbox, BoxActive.IsChecked == true);
		BuildBoxesPanel();
	}

	// One number to type (Enter or leaving the box puts it in)
	private FrameworkElement NumberRow(string label, float value, string tip, Action<float> commit)
	{
		var box = new TextBox { Text = value.ToString("0.##", CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center };
		void Commit()
		{
			if (_syncingBoxes) return;
			if (!float.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float typed)
				&& !float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out typed)) return;
			if (MathF.Abs(typed - value) < 1e-4f) return;
			commit(typed);
			BuildBoxesPanel();
		}
		box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Viewport.Focus(); } };
		box.LostFocus += (_, _) => Commit();
		var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), ToolTip = tip };
		var text = new TextBlock { Text = label, Foreground = AdjustEditor.LabelBrush, Width = 64, VerticalAlignment = VerticalAlignment.Center };
		DockPanel.SetDock(text, Dock.Left);
		row.Children.Add(text);
		row.Children.Add(box);
		return row;
	}

	private static float Degrees(float radians) => radians * 180f / MathF.PI;
	private static float Radians(float degrees) => degrees * MathF.PI / 180f;

	private void BuildBoxFields()
	{
		BoxFields.Children.Clear();
		switch (_boxItem)
		{
			case Hitbox h:
				BoxFields.Children.Add(NumberRow("X", h.Offset.X, "Its middle, along its bone", v => _session.PlaceBoxItem(h, offset: h.Offset with { X = v })));
				BoxFields.Children.Add(NumberRow("Y", h.Offset.Y, "Its middle, across its bone", v => _session.PlaceBoxItem(h, offset: h.Offset with { Y = v })));
				if (h.Shape == HitboxShape.Box)
				{
					BoxFields.Children.Add(NumberRow("Width", h.Size.X, "Along its own sides", v => _session.PlaceBoxItem(h, size: h.Size with { X = v })));
					BoxFields.Children.Add(NumberRow("Height", h.Size.Y, "Along its own sides", v => _session.PlaceBoxItem(h, size: h.Size with { Y = v })));
					BoxFields.Children.Add(NumberRow("Angle", Degrees(h.Rotation), "Degrees, from its bone's direction", v => _session.PlaceBoxItem(h, rotation: Radians(v))));
				}
				else
					BoxFields.Children.Add(NumberRow("Diameter", h.Size.X, "Across the circle", v => _session.PlaceBoxItem(h, size: new Vector2(v))));
				break;
			case RigPoint p:
				BoxFields.Children.Add(NumberRow("X", p.Offset.X, "Along its bone", v => _session.PlaceBoxItem(p, offset: p.Offset with { X = v })));
				BoxFields.Children.Add(NumberRow("Y", p.Offset.Y, "Across its bone", v => _session.PlaceBoxItem(p, offset: p.Offset with { Y = v })));
				BoxFields.Children.Add(NumberRow("Facing", Degrees(p.Rotation), "Degrees, from its bone's direction", v => _session.PlaceBoxItem(p, rotation: Radians(v))));
				break;
		}
	}

	private void BuildBoxesPanel()
	{
		if (!_boxesView) return;
		if (_boxItem is Hitbox gone && !_skeleton.Hitboxes.Contains(gone)) _boxItem = null;
		if (_boxItem is RigPoint lost && !_skeleton.Points.Contains(lost)) _boxItem = null;
		_syncingBoxes = true;
		BoxList.Items.Clear();
		foreach (var h in _skeleton.Hitboxes)
		{
			var c = HitboxRenderer.ColorOf(h.Kind);
			var item = new ListBoxItem
			{
				Content = $"{(h.Shape == HitboxShape.Box ? "■" : "●")} {h.Name}   {h.Kind} · {h.Bone.Name}{(h.SetupActive ? "" : " · off at rest")}",
				Tag = h,
				Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.Red, c.Green, c.Blue))
			};
			BoxList.Items.Add(item);
			if (h == _boxItem) BoxList.SelectedItem = item;
		}
		foreach (var p in _skeleton.Points)
		{
			var item = new ListBoxItem { Content = $"✛ {p.Name}   point · {p.Bone.Name}", Tag = p, Foreground = AdjustEditor.LabelBrush };
			BoxList.Items.Add(item);
			if (p == _boxItem) BoxList.SelectedItem = item;
		}

		var hitbox = _boxItem as Hitbox;
		BoxSettings.Visibility = _boxItem != null ? Visibility.Visible : Visibility.Collapsed;
		BoxDeleteButton.IsEnabled = _boxItem != null;
		BoxHitboxOnly.Visibility = hitbox != null ? Visibility.Visible : Visibility.Collapsed;
		if (!BoxName.IsKeyboardFocused) BoxName.Text = hitbox?.Name ?? (_boxItem as RigPoint)?.Name ?? "";
		BoxBoneChoice.Items.Clear();
		foreach (var bone in _skeleton.AllBones()) BoxBoneChoice.Items.Add(bone.Name);
		BoxBoneChoice.SelectedItem = hitbox?.Bone.Name ?? (_boxItem as RigPoint)?.Bone.Name;
		BoxKind.Items.Clear();
		foreach (var kind in Hitbox.CommonKinds.Concat(_skeleton.Hitboxes.Select(h => h.Kind)).Distinct(StringComparer.OrdinalIgnoreCase)) BoxKind.Items.Add(kind);
		if (!BoxKind.IsKeyboardFocusWithin) BoxKind.Text = hitbox?.Kind ?? "";
		BoxShape.SelectedIndex = hitbox?.Shape == HitboxShape.Circle ? 1 : 0;
		BoxActive.IsChecked = hitbox?.SetupActive ?? true;
		BoxKeyRow.Visibility = hitbox != null && _state.AnimateMode ? Visibility.Visible : Visibility.Collapsed;
		BuildBoxFields();
		_syncingBoxes = false;

		BoxHint.Text = _boxItem switch
		{
			null when _skeleton.Hitboxes.Count == 0 && _skeleton.Points.Count == 0 =>
				"Select a bone (or a part: the box goes round it), then + Hitbox or + Point. Hitboxes and points move with their bones; sprite sheets export them per frame.",
			null => "Click one in the viewport or the list. Drag to move it; a picked box's corner resizes it.",
			Hitbox h when _state.AnimateMode => $"Animate: {h.Name} is {(h.Active ? "on" : "off")} at frame {_state.Frame}. On / Off from here key it.",
			Hitbox => "Drag it in the viewport (a corner resizes, round its middle), or type its numbers. Positions are on its bone.",
			_ => "A point: where the game spawns or holds things, facing the way its line shows."
		};
	}
}
