using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Animator.Desktop;

// The Rig tab on the left: inside an object, its bones (as a tree, by indent) with the parts on
// each, or its layers (every part in drawing order, front first: a drawn object always shows
// these); at scene level, the scene's objects. Picking one there picks it in the viewport, and
// the other way round; a double-click on an object opens it. Over the list: + Part, Bind (or
// Give bones, for a drawn object); under it: whether parts stay put when bones move, and
// whether animations stay as they were. Rows are dragged: a part onto a bone puts it there, a
// bone onto another hangs it there (Bones), a part up or down changes the order (Layers).
public sealed partial class MainWindow
{
	internal readonly ListBox RigList = new();
	internal readonly ToggleButton BonesView = new() { Content = "Bones" };
	internal readonly ToggleButton LayersView = new() { Content = "Layers" };
	internal readonly TextBlock RigHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
	private readonly Button _addPart = new() { Content = "+ Part ▾" };
	private readonly Button _bind = new() { Content = "Bind ▾" };
	private readonly Button _giveBones = new() { Content = "Give it bones ▾" };
	internal readonly CheckBox KeepPartsBox = new() { Content = "Parts stay put when bones move" };
	internal readonly CheckBox KeepAnimationsBox = new() { Content = "Animations stay as they were" };
	private Control _rigTools = null!;
	private bool _syncingRig;
	private bool _layersView;                                         // a rigged object's list shows its layers
	private IReadOnlyList<Slot> _listedOrder = Array.Empty<Slot>();   // the order the list was made in
	private bool _listedAnimating;                                    // ...and in which mode

	internal bool ShowingLayers => !SceneLevel && (IsDrawn || _layersView);

	private bool IsDrawn => _session.Active?.Kind == ObjectKind.Drawn;

	private Control BuildRigList()
	{
		RigList.SelectionMode = SelectionMode.Single;
		RigList.SelectionChanged += (_, _) =>
		{
			if (_syncingRig || RigList.SelectedItem is not ListBoxItem { Tag: { } picked }) return;
			Pick(picked);
		};
		RigList.DoubleTapped += (_, _) =>
		{
			if (SceneLevel && RigList.SelectedItem is ListBoxItem { Tag: SceneObject obj }) EnterObject(obj);
		};
		RigList.AddHandler(PointerPressedEvent, RigPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
		RigList.AddHandler(PointerMovedEvent, RigMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
		RigList.AddHandler(PointerReleasedEvent, RigReleased, RoutingStrategies.Tunnel, handledEventsToo: true);

		BonesView.Click += (_, _) => { _layersView = false; RefreshRigList(); };
		LayersView.Click += (_, _) => { _layersView = true; RefreshRigList(); };
		ToolTip.SetTip(BonesView, "The bones as a tree, with the parts on each: drag a part onto a bone to put it there, a bone onto another to hang it there");
		ToolTip.SetTip(LayersView, "Every part in the order it's drawn, front first: drag one up or down (in Animate: keys the order at the playhead)");
		_addPart.Click += (_, _) => ShowAddPartMenu(_addPart);
		_bind.Click += (_, _) => ShowBindMenu(_bind);
		_giveBones.Click += (_, _) => ShowGiveBonesMenu(_giveBones);
		ToolTip.SetTip(_giveBones, "Make this drawn object a rigged one: every part stays exactly where it is");
		ToolTip.SetTip(KeepPartsBox, "Moving or turning a bone at rest leaves its parts (and the bones on it) where they are, to fit bones to finished art");
		ToolTip.SetTip(KeepAnimationsBox, "Changing the rig keeps every animation looking as it did (keys are made up for the change)");
		KeepPartsBox.IsCheckedChanged += (_, _) =>
		{
			if (_syncingRig) return;
			_tool.KeepPartsInPlace = _settings.KeepPartsInPlace = KeepPartsBox.IsChecked == true;
			SaveSettingsQuietly();
		};
		KeepAnimationsBox.IsCheckedChanged += (_, _) =>
		{
			if (_syncingRig) return;
			State.KeepAnimations = _settings.KeepAnimations = KeepAnimationsBox.IsChecked == true;
			SaveSettingsQuietly();
		};

		var views = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 0, 4) };
		views.Children.Add(BonesView);
		views.Children.Add(LayersView);
		var buttons = new WrapPanel();
		foreach (var b in new Control[] { _addPart, _bind, _giveBones })
		{
			b.Margin = new Thickness(0, 0, 4, 4);
			buttons.Children.Add(b);
		}
		var top = new StackPanel { Margin = new Thickness(4, 4, 4, 0), Children = { views, buttons } };
		var bottom = new StackPanel { Margin = new Thickness(4), Spacing = 2, Children = { RigHint, KeepPartsBox, KeepAnimationsBox } };
		_rigTools = top;
		var panel = new DockPanel();
		DockPanel.SetDock(top, Dock.Top);
		DockPanel.SetDock(bottom, Dock.Bottom);
		panel.Children.Add(top);
		panel.Children.Add(bottom);
		panel.Children.Add(RigList);
		_bottomRigTools = bottom;
		return panel;
	}

	private Control _bottomRigTools = null!;

	private void SaveSettingsQuietly()
	{
		try { _settings.Save(); } catch (Exception) { }   // (not being able to remember it isn't worth interrupting anyone for)
	}

	internal void ShowRigHint(string text) => RigHint.Text = text;

	private void Pick(object picked)
	{
		switch (picked)
		{
			case SceneObject obj:
				_session.SceneState.Selected = obj;
				break;
			case Bone bone:
				State.Selected = bone;
				State.SelectedSlot = null;
				break;
			case Slot slot:
				State.SelectedSlot = slot;
				State.Selected = slot.Bone;
				break;
		}
	}

	// The list made again (another object open, a bone added or taken away, the order changed…)
	internal void RefreshRigList()
	{
		_syncingRig = true;
		try
		{
			var items = new List<ListBoxItem>();
			if (SceneLevel)
			{
				foreach (var obj in Scene.Objects)
					items.Add(Row(obj.Name + (obj.Visible ? "" : "  (hidden)"), obj, 0, bold: true));
			}
			else if (ShowingLayers)
			{
				_listedAnimating = State.AnimateMode;
				_listedOrder = _session.CurrentOrder().ToList();
				for (int i = _listedOrder.Count - 1; i >= 0; i--) items.Add(LayerRow(_listedOrder[i]));
			}
			else
			{
				var rig = State.Skeleton;
				void AddBone(Bone bone, int depth)
				{
					items.Add(Row("🦴 " + bone.Name, bone, depth, bold: false));
					foreach (var slot in rig.Slots.Where(s => s.Bone == bone))
						items.Add(Row("▣ " + slot.Name, slot, depth + 1, bold: false, part: true));
					foreach (var child in bone.Children) AddBone(child, depth + 1);
				}
				foreach (var root in rig.Roots) AddBone(root, 0);
			}
			foreach (var item in items) item.ContextRequested += RowMenu;
			RigList.ItemsSource = items;
			_listed = items;
			_listedPick = null;   // (picked again in the new rows, below)
			SyncRigTools();
		}
		finally
		{
			_syncingRig = false;
		}
		SyncRigSelection();
	}

	private List<ListBoxItem> _listed = new();
	private object? _listedPick;

	private static readonly IBrush PartColour = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(255, 190, 110));
	private static readonly IBrush MaskColour = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(200, 150, 255));

	private static ListBoxItem Row(string text, object tag, int depth, bool bold, bool part = false)
	{
		var label = new TextBlock { Text = text, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
		if (part) label.Foreground = PartColour;   // (bones keep the theme's colour)
		return new ListBoxItem { Content = label, Tag = tag, Padding = new Thickness(8 + depth * 14, 3, 8, 3) };
	}

	// A part in the layers: its bone, whether it masks or is clipped, the skins it's in
	private ListBoxItem LayerRow(Slot slot)
	{
		var label = new TextBlock();
		label.Inlines!.Add(new Avalonia.Controls.Documents.Run("▣ " + slot.Name) { Foreground = PartColour });
		void Note(string text, IBrush? brush = null) =>
			label.Inlines.Add(new Avalonia.Controls.Documents.Run(text) { Foreground = brush ?? Brushes.Gray });
		if (!IsDrawn) Note($"   on {slot.Bone.Name}");
		if (slot.IsMask) Note($"   ◐ mask{(slot.MaskShows ? ", shows" : "")}", MaskColour);
		else if (MaskOps.MaskOf(State.Skeleton, slot) is { } mask) Note($"   ↳ clipped by {mask.Name}", MaskColour);
		if (SkinOps.SkinsWith(State.Skeleton, slot) is { Count: > 0 } skins)
			Note($"   skin{(skins.Count == 1 ? "" : "s")}: {string.Join(", ", skins.Select(s => s.Name))}");
		return new ListBoxItem { Content = label, Tag = slot, Padding = new Thickness(8, 3) };
	}

	// What's over and under the list goes with what it shows
	private void SyncRigTools()
	{
		bool inside = !SceneLevel;
		_rigTools.IsVisible = _bottomRigTools.IsVisible = inside;
		BonesView.IsVisible = inside && !IsDrawn;
		BonesView.IsChecked = !ShowingLayers;
		LayersView.IsChecked = ShowingLayers;
		_bind.IsVisible = inside && !IsDrawn;
		_giveBones.IsVisible = inside && IsDrawn;
		KeepPartsBox.IsVisible = KeepAnimationsBox.IsVisible = inside && !IsDrawn;
		KeepPartsBox.IsChecked = _tool.KeepPartsInPlace;
		KeepAnimationsBox.IsChecked = State.KeepAnimations;
		RigHint.Text = LayersHintText();
	}

	// What the line under the list says in the layers: what dragging does now
	private string LayersHintText()
	{
		if (!ShowingLayers || State.Skeleton.Slots.Count < 2) return "";
		if (State.AnimateMode) return $"Animate: dragging a part up or down keys the order at frame {State.Frame}.";
		return State.Skeleton.DrawOrder.SequenceEqual(State.Skeleton.Slots)
			? "Drag a part up or down to change the order (the top one is in front)."
			: "The rest order. This animation draws them in another order here: Animate (K) shows and keys that one.";
	}

	// Every frame: what's picked in the viewport is picked in the list, and the layers follow the
	// order as it changes (playing past a key, switching mode)
	private void SyncRigSelection()
	{
		if (ShowingLayers && (_listedAnimating != State.AnimateMode || !_session.CurrentOrder().SequenceEqual(_listedOrder)))
		{
			RefreshRigList();
			return;
		}
		object? picked = SceneLevel ? _session.SceneState.Selected : (object?)State.SelectedSlot ?? State.Selected;
		if (picked == _listedPick) return;
		_listedPick = picked;
		_syncingRig = true;
		try
		{
			RigList.SelectedItem = _listed.FirstOrDefault(i => i.Tag == picked);
		}
		finally
		{
			_syncingRig = false;
		}
	}

	// ======================= dragging rows =======================

	private Point? _rigPress;
	private object? _rigDragged;      // the Slot or Bone pressed on
	private bool _rigDragging;
	private ListBoxItem? _dropMark;

	private static ListBoxItem? RowAt(ListBox list, Point at) =>
		(list.InputHitTest(at) as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

	private void RigPressed(object? sender, PointerPressedEventArgs e)
	{
		_rigPress = null;
		_rigDragged = null;
		_rigDragging = false;
		if (SceneLevel || !e.GetCurrentPoint(RigList).Properties.IsLeftButtonPressed) return;
		var at = e.GetPosition(RigList);
		if (RowAt(RigList, at)?.Tag is not (Slot or Bone) pressed) return;
		_rigPress = at;
		_rigDragged = pressed;
	}

	private void RigMoved(object? sender, PointerEventArgs e)
	{
		if (_rigPress is not { } press || _rigDragged == null) return;
		var at = e.GetPosition(RigList);
		if (!_rigDragging)
		{
			if (Math.Abs(at.X - press.X) < 4 && Math.Abs(at.Y - press.Y) < 4) return;
			_rigDragging = true;
			e.Pointer.Capture(RigList);
		}
		var row = RowAt(RigList, at);
		bool upper = row != null && e.GetPosition(row).Y < row.Bounds.Height / 2;
		MarkDrop(row != null && CanDrop(_rigDragged, row.Tag, upper) ? row : null, upper);
		e.Handled = true;
	}

	private void RigReleased(object? sender, PointerReleasedEventArgs e)
	{
		var dragged = _rigDragged;
		bool dragging = _rigDragging;
		_rigPress = null;
		_rigDragged = null;
		_rigDragging = false;
		MarkDrop(null, false);
		if (!dragging || dragged == null) return;
		e.Pointer.Capture(null);
		var row = RowAt(RigList, e.GetPosition(RigList));
		if (row?.Tag is { } target) DropOnRow(dragged, target, e.GetPosition(row).Y < row.Bounds.Height / 2);
		e.Handled = true;
	}

	// A line over (in front of) or under (behind) the row a drop would land by; in the bones, the
	// row itself
	private void MarkDrop(ListBoxItem? row, bool upper)
	{
		if (_dropMark != null && _dropMark != row) _dropMark.BorderThickness = new Thickness(0);
		_dropMark = row;
		if (row == null) return;
		row.BorderBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(80, 220, 255));
		row.BorderThickness = !ShowingLayers ? new Thickness(2) : upper ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 2);
	}

	// The picked ones go along with one of them dragged
	private List<Slot> DraggedAlong(Slot slot) =>
		State.IsPicked(slot) ? State.PickedSlots.ToList() : new List<Slot> { slot };

	private List<Bone> DraggedAlong(Bone bone) =>
		State.IsPicked(bone) ? State.PickedBones.ToList() : new List<Bone> { bone };

	// The bone a row means as a drop target (a bone's row, or the bone of a part's row)
	private static Bone? BoneOfRow(object target) => target switch
	{
		Bone b => b,
		Slot s => s.Bone,
		_ => null
	};

	private bool CanDrop(object dragged, object? target, bool upper)
	{
		if (target == null) return false;
		if (ShowingLayers) return dragged is Slot slot && target is Slot onto && !DraggedAlong(slot).Contains(onto);
		var bone = BoneOfRow(target);
		return dragged switch
		{
			Slot slot => bone != null && slot.Bone != bone,
			Bone moving => bone != null && bone != moving && !bone.IsDescendantOf(moving) && moving.Parent != bone,
			_ => false
		};
	}

	// A drop: in the layers, the part (and the picked ones with it) in front of (upper half) or
	// behind the target; in the bones, parts onto the bone, bones hung from it (all staying put)
	internal void DropOnRow(object dragged, object target, bool upper)
	{
		if (!CanDrop(dragged, target, upper)) return;
		if (ShowingLayers)
		{
			var moving = DraggedAlong((Slot)dragged);
			_session.PlaceInOrder(moving, (Slot)target, inFront: upper);
			if (moving.Count == 1) Pick(dragged);
			RefreshRigList();
			return;
		}
		var onto = BoneOfRow(target)!;
		if (dragged is Slot slot)
		{
			var parts = DraggedAlong(slot).Where(s => s.Bone != onto).ToList();
			_session.PutPartsOnBone(parts, onto);
			if (parts.Count == 1)
			{
				State.SelectedSlot = slot;
				State.Selected = onto;
			}
		}
		else if (dragged is Bone bone)
		{
			// The picked bones no other dragged bone is above (those come along anyway), and not
			// the one they'd hang from or anything above it
			var bones = RestEdits.TopOf(DraggedAlong(bone)).Where(b => b != onto && !onto.IsDescendantOf(b) && b.Parent != onto).ToList();
			if (bones.Count == 0) bones.Add(bone);
			_session.HangBones(bones, onto, _tool.KeepPartsInPlace);
			if (bones.Count == 1)
			{
				State.Selected = bone;
				State.SelectedSlot = null;
			}
		}
		RefreshRigList();
	}

	// Right-click on a part's row: its place in the order, and masking
	private void RowMenu(object? sender, ContextRequestedEventArgs e)
	{
		if (sender is not ListBoxItem { Tag: Slot slot } row) return;
		e.Handled = true;
		OrderMenu(slot).ShowAt(row, showAtPointer: true);
	}
}
