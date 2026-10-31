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

	private sealed class BoxDrag
	{
		public required object Item;
		public required bool Resize;
		public required StructureSnapshot Before;
		public required Vector2 Start;          // where the mouse went down, in the object's space
		public required Vector2 StartOffset;
		public required Vector2 StartSize;
		public bool Moved;
	}
	private BoxDrag? _boxDrag;

	private bool ShowingHitboxes => !_sceneLevel && (_showHitboxes || _boxesView);

	private void DrawHitboxes(SKCanvas canvas)
	{
		if (!ShowingHitboxes || (_skeleton.Hitboxes.Count == 0 && _skeleton.Points.Count == 0)) return;
		DrawInSpace(canvas, _camera.Space, () => HitboxRenderer.Draw(canvas, _skeleton, _renderer.Zoom, _boxesView ? _boxItem : null, _boxesView ? _boxHover : null));
	}

	private void ToggleHitboxes()
	{
		_showHitboxes = !_showHitboxes;
		AnimHint.Text = _showHitboxes ? "Hitboxes and points show (the Boxes view edits them)." : "";
	}

	private void EditBoxes(string name, Func<bool> change)
	{
		if (_sceneLevel) return;
		_state.EditStructure(() => change() ? name : null);
	}

	// ======================= adding and deleting =======================

	private Bone? BoxBone() => _state.Selected ?? _state.SelectedSlot?.Bone ?? _skeleton.Roots.FirstOrDefault();

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
		Hitbox? made = null;
		var part = _state.SelectedSlot;
		EditBoxes($"Add a {kind} {(shape == HitboxShape.Box ? "box" : "circle")} on {bone.Name}", () =>
		{
			made = HitboxOps.AddHitbox(_skeleton, bone, shape, kind, part != null ? $"{part.Name} {kind}" : null);
			if (part != null && part.Bone == bone) FitBox(made, part);   // round the selected part, when it's on that bone
			return true;
		});
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
		RigPoint? made = null;
		EditBoxes($"Add a point on {bone.Name}", () => { made = HitboxOps.AddPoint(_skeleton, bone); return true; });
		_boxItem = made;
		BuildBoxesPanel();
		BoxHint.Text = $"{made?.Name}: at the tip of {bone.Name}. Drag it, and type which way it faces.";
	}

	private void BoxDelete_Click(object sender, RoutedEventArgs e)
	{
		switch (_boxItem)
		{
			case Hitbox hitbox:
				EditBoxes($"Delete the hitbox {hitbox.Name}", () => { HitboxOps.DeleteHitbox(_skeleton, _state.Animations, hitbox); return true; });
				break;
			case RigPoint point:
				EditBoxes($"Delete the point {point.Name}", () => _skeleton.Points.Remove(point));
				break;
			default: return;
		}
		_boxItem = null;
		BuildBoxesPanel();
	}

	// Round the picked part's visible pixels (as it rests)
	private void BoxFit_Click(object sender, RoutedEventArgs e)
	{
		if (_boxItem is not Hitbox hitbox) return;
		if (_state.SelectedSlot is not { } part)
		{
			BoxHint.Text = "Pick the part to fit it round (in the viewport, or the Bones or Layers view): the Boxes view keeps its hitbox selected.";
			return;
		}
		bool fitted = false;
		EditBoxes($"Fit {hitbox.Name} round {part.Name}", () =>
		{
			_skeleton.SetToSetupPose();
			_skeleton.UpdateWorld();
			fitted = FitBox(hitbox, part);
			return fitted;
		});
		_state.SetFrame(_state.Frame);   // (back to how the animation poses it)
		BuildBoxesPanel();
		if (!fitted) BoxHint.Text = $"{part.Name} shows nothing at rest to fit round.";
	}

	private bool FitBox(Hitbox hitbox, Slot part)
	{
		(float, float, float, float)? visible = null;
		if (part.SetupAttachment is { } name && _skeleton.FindAttachment(part.Name, name) is { } drawing
			&& _skeleton.Assets.TryGetValue(drawing.AssetId, out var asset) && _assets.Get(asset, false) is { } bitmap)
			visible = VisibleRect(bitmap);
		return HitboxOps.FitToPart(_skeleton, hitbox, part, visible);
	}

	// The rectangle of a picture's pixels that show (null: none do, or all of it)
	private static (float X, float Y, float W, float H)? VisibleRect(SKBitmap bitmap)
	{
		int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
		for (int y = 0; y < bitmap.Height; y++)
			for (int x = 0; x < bitmap.Width; x++)
				if (bitmap.GetPixel(x, y).Alpha > 8)
				{
					if (x < minX) minX = x;
					if (x > maxX) maxX = x;
					if (y < minY) minY = y;
					if (y > maxY) maxY = y;
				}
		return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
	}

	// Animate mode: on or off from the playhead on
	private void BoxOnHere_Click(object sender, RoutedEventArgs e) => KeyBox(true);
	private void BoxOffHere_Click(object sender, RoutedEventArgs e) => KeyBox(false);

	private void KeyBox(bool on)
	{
		if (_boxItem is not Hitbox hitbox || !_state.AnimateMode) return;
		int frame = _state.Frame;
		EditBoxes($"{hitbox.Name} {(on ? "on" : "off")} @ frame {frame}", () => HitboxOps.KeyActive(_state.Animation, hitbox, frame, _state.Fps, on));
		_state.SetFrame(frame);
		BuildBoxesPanel();
	}

	// ======================= the viewport =======================

	// What's under the mouse: the picked box's handles first, then points, then the smallest box
	private (object Item, bool Resize)? BoxAt(Vector2 screen)
	{
		const float Reach = 8f;
		if (_boxItem is Hitbox picked)
			foreach (var handle in HitboxRenderer.Handles(picked))
				if (Vector2.Distance(_camera.WorldToScreen(handle), screen) <= Reach) return (picked, true);
		foreach (var point in _skeleton.Points.AsEnumerable().Reverse())
			if (Vector2.Distance(_camera.WorldToScreen(point.WorldPosition), screen) <= Reach) return (point, false);
		var world = _camera.ScreenToWorld(screen);
		var inside = _skeleton.Hitboxes.Where(h => h.Contains(world)).OrderBy(h => h.Size.X * h.Size.Y).FirstOrDefault();
		return inside != null ? (inside, false) : null;
	}

	// True: the mouse went down on a hitbox or point (the Boxes view open), and drags it
	private bool BoxMouseDown(Vector2 screen, MouseButton button)
	{
		if (!_boxesView || _sceneLevel || button != MouseButton.Left || _tool.Tool == ViewTool.Paint) return false;
		if (BoxAt(screen) is not { } hit) return false;
		_boxItem = hit.Item;
		_boxDrag = new BoxDrag
		{
			Item = hit.Item,
			Resize = hit.Resize,
			Before = _state.CaptureStructure(),
			Start = _camera.ScreenToWorld(screen),
			StartOffset = hit.Item is Hitbox h ? h.Offset : ((RigPoint)hit.Item).Offset,
			StartSize = hit.Item is Hitbox s ? s.Size : Vector2.Zero
		};
		BuildBoxesPanel();
		return true;
	}

	private void BoxMouseMove(Vector2 screen)
	{
		if (_boxDrag is not { } drag) return;
		var world = _camera.ScreenToWorld(screen);
		var bone = drag.Item is Hitbox hb ? hb.Bone : ((RigPoint)drag.Item).Bone;
		if (!Matrix3x2.Invert(bone.World, out var toBone)) return;
		if (drag.Resize && drag.Item is Hitbox box)
		{
			// Round its middle: the corner (or edge) follows the mouse
			if (!Matrix3x2.Invert(box.World, out var toBox)) return;
			var local = Vector2.Transform(world, toBox);
			box.Size = box.Shape == HitboxShape.Box
				? new Vector2(MathF.Max(1f, 2f * MathF.Abs(local.X)), MathF.Max(1f, 2f * MathF.Abs(local.Y)))
				: new Vector2(MathF.Max(1f, 2f * local.Length()));
		}
		else
		{
			var offset = drag.StartOffset + Vector2.TransformNormal(world - drag.Start, toBone);
			if (drag.Item is Hitbox moved) moved.Offset = offset;
			else ((RigPoint)drag.Item).Offset = offset;
		}
		drag.Moved = true;
	}

	private void BoxMouseUp()
	{
		if (_boxDrag is not { } drag) return;
		_boxDrag = null;
		string name = drag.Item is Hitbox h ? h.Name : ((RigPoint)drag.Item).Name;
		if (drag.Moved) _state.PushStructure(drag.Before, drag.Resize ? $"Resize {name}" : $"Move {name}");
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
				EditBoxes($"Rename the hitbox to {name}", () => HitboxOps.RenameHitbox(_skeleton, _state.Animations, hitbox, name));
				break;
			case RigPoint point when name.Length > 0 && name != point.Name:
				if (_skeleton.FindPoint(name) != null) { BoxHint.Text = $"Another point is called \"{name}\"."; BoxName.Text = point.Name; return; }
				EditBoxes($"Rename the point to {name}", () => HitboxOps.RenamePoint(_skeleton, point, name));
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
		string kind = BoxKind.Text.Trim();
		if (kind.Length == 0 || kind == hitbox.Kind) return;
		EditBoxes($"Make {hitbox.Name} a {kind} box", () => { hitbox.Kind = kind; return true; });
		BuildBoxesPanel();
	}

	private void BoxShape_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingBoxes || _boxItem is not Hitbox hitbox) return;
		var shape = BoxShape.SelectedIndex == 1 ? HitboxShape.Circle : HitboxShape.Box;
		if (shape == hitbox.Shape) return;
		EditBoxes($"Make {hitbox.Name} a {(shape == HitboxShape.Box ? "box" : "circle")}", () =>
		{
			hitbox.Shape = shape;
			if (shape == HitboxShape.Circle) hitbox.Size = new Vector2((hitbox.Size.X + hitbox.Size.Y) / 2f);
			return true;
		});
		BuildBoxesPanel();
	}

	private void BoxBone_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingBoxes || BoxBoneChoice.SelectedItem is not string boneName || _skeleton.FindBone(boneName) is not { } bone) return;
		switch (_boxItem)
		{
			case Hitbox hitbox: EditBoxes($"Put {hitbox.Name} on {bone.Name}", () => HitboxOps.SetBone(hitbox, bone)); break;
			case RigPoint point: EditBoxes($"Put {point.Name} on {bone.Name}", () => HitboxOps.SetBone(point, bone)); break;
		}
		BuildBoxesPanel();
	}

	private void BoxActive_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingBoxes || _boxItem is not Hitbox hitbox) return;
		bool on = BoxActive.IsChecked == true;
		EditBoxes($"{hitbox.Name} {(on ? "on" : "off")} at rest", () => { hitbox.SetupActive = on; return true; });
		_state.SetFrame(_state.Frame);
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
				BoxFields.Children.Add(NumberRow("X", h.Offset.X, "Its middle, along its bone", v => EditBoxes($"Move {h.Name}", () => { h.Offset = h.Offset with { X = v }; return true; })));
				BoxFields.Children.Add(NumberRow("Y", h.Offset.Y, "Its middle, across its bone", v => EditBoxes($"Move {h.Name}", () => { h.Offset = h.Offset with { Y = v }; return true; })));
				if (h.Shape == HitboxShape.Box)
				{
					BoxFields.Children.Add(NumberRow("Width", h.Size.X, "Along its own sides", v => EditBoxes($"Resize {h.Name}", () => { h.Size = h.Size with { X = MathF.Max(1f, v) }; return true; })));
					BoxFields.Children.Add(NumberRow("Height", h.Size.Y, "Along its own sides", v => EditBoxes($"Resize {h.Name}", () => { h.Size = h.Size with { Y = MathF.Max(1f, v) }; return true; })));
					BoxFields.Children.Add(NumberRow("Angle", Degrees(h.Rotation), "Degrees, from its bone's direction", v => EditBoxes($"Turn {h.Name}", () => { h.Rotation = Radians(v); return true; })));
				}
				else
					BoxFields.Children.Add(NumberRow("Diameter", h.Size.X, "Across the circle", v => EditBoxes($"Resize {h.Name}", () => { h.Size = new Vector2(MathF.Max(1f, v)); return true; })));
				break;
			case RigPoint p:
				BoxFields.Children.Add(NumberRow("X", p.Offset.X, "Along its bone", v => EditBoxes($"Move {p.Name}", () => { p.Offset = p.Offset with { X = v }; return true; })));
				BoxFields.Children.Add(NumberRow("Y", p.Offset.Y, "Across its bone", v => EditBoxes($"Move {p.Name}", () => { p.Offset = p.Offset with { Y = v }; return true; })));
				BoxFields.Children.Add(NumberRow("Facing", Degrees(p.Rotation), "Degrees, from its bone's direction", v => EditBoxes($"Turn {p.Name}", () => { p.Rotation = Radians(v); return true; })));
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
