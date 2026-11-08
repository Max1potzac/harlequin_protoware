using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Animator.Desktop;

// The left panel, as the Windows editor has it: the scene's objects on top (see Scene), then at
// scene level the picked object's settings, and inside an object its rig: whether parts stay put
// when bones move and animations stay as they were, + Bone, + Part, Bind, Delete (Build only),
// and six views of it: its bones (as a tree, by indent) with the parts on each, its order (every
// part in drawing order, front first: a drawn object always shows these), its colour layers,
// poses, skins, and hitboxes. Picking a row picks it in the viewport, and the other way round.
// Rows are dragged: a part onto a bone puts it there, a bone onto another hangs it there (Bones),
// a part up or down changes the order (Order).
public sealed partial class MainWindow
{
	internal readonly ListBox RigList = new();
	internal readonly ToggleButton BonesView = new() { Content = "Bones" };
	internal readonly ToggleButton LayersView = new() { Content = "Order" };
	internal readonly ToggleButton PosesView = new() { Content = "Poses" };
	internal readonly ToggleButton SkinsView = new() { Content = "Skins" };
	internal readonly TextBlock RigTitle = new() { Text = "Rig", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
	internal readonly Button DeleteButton = new() { Content = "Delete ▾" };
	private Control _buildButtons = null!, _rigSection = null!, _sceneSection = null!, _posesPanel = null!, _skinsPanel = null!;
	private bool _posesViewOn, _skinsViewOn;
	internal readonly TextBlock ObjectNone = new() { Text = "Click an object in the viewport, or pick it in the list.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
	private readonly TextBlock _objectHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange };

	// The views of the rig
	internal enum RigView { Bones, Layers, Colour, Poses, Skins, Boxes }

	internal void ShowRigView(RigView view)
	{
		_layersView = view == RigView.Layers;
		_colourViewOn = view == RigView.Colour;
		_posesViewOn = view == RigView.Poses;
		_skinsViewOn = view == RigView.Skins;
		_boxesViewOn = view == RigView.Boxes;
		RefreshRigList();
	}

	internal bool ShowingPoses => _posesViewOn && !SceneLevel;
	internal bool ShowingSkins => _skinsViewOn && !SceneLevel;
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

	internal bool ShowingLayers => !SceneLevel && !_colourViewOn && !_boxesViewOn && !_posesViewOn && !_skinsViewOn && (IsDrawn || _layersView);

	private bool IsDrawn => _session.Active?.Kind == ObjectKind.Drawn;

	// The whole left panel: the objects, then the picked object's settings (scene level) or the rig
	private Control BuildLeftPanel()
	{
		var objects = BuildObjectsSection();
		_rigSection = BuildRigSection();
		_sceneSection = new ScrollViewer
		{
			Content = new StackPanel { Margin = new Thickness(6, 2, 6, 6), Spacing = 4, Children = { ObjectNone, _objectProps, _objectHint } }
		};
		var panel = new DockPanel();
		DockPanel.SetDock(objects, Dock.Top);
		panel.Children.Add(objects);
		panel.Children.Add(new Grid { Children = { _sceneSection, _rigSection } });   // (one or the other)
		return panel;
	}

	private Control BuildRigSection()
	{
		RigList.SelectionMode = SelectionMode.Single;
		RigList.SelectionChanged += (_, _) =>
		{
			if (_syncingRig || RigList.SelectedItem is not ListBoxItem { Tag: { } picked }) return;
			Pick(picked);
		};
		RigList.AddHandler(PointerPressedEvent, RigPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
		RigList.AddHandler(PointerMovedEvent, RigMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
		RigList.AddHandler(PointerReleasedEvent, RigReleased, RoutingStrategies.Tunnel, handledEventsToo: true);

		BonesView.Click += (_, _) => ShowRigView(RigView.Bones);
		LayersView.Click += (_, _) => ShowRigView(RigView.Layers);
		PosesView.Click += (_, _) => ShowRigView(RigView.Poses);
		SkinsView.Click += (_, _) => ShowRigView(RigView.Skins);
		ToolTip.SetTip(BonesView, "The bones as a tree, with the parts on each: drag a part onto a bone to put it there, a bone onto another to hang it there");
		ToolTip.SetTip(LayersView, "Every part in the order it's drawn, front first: drag one up or down (in Animate: keys the order at the playhead)");
		ToolTip.SetTip(PosesView, "Poses kept by name, to put the rig that way again (mirrored too), and mirroring: the pose, copy and paste mirrored, and symmetrizing the rig");
		ToolTip.SetTip(SkinsView, "Skins: drawings for some parts, shown when the skin is on. Categories of alternatives (one at a time) and overlays that show over the others.");
		_addPart.Click += (_, _) => ShowAddPartMenu(_addPart);
		_bind.Click += (_, _) => ShowBindMenu(_bind);
		_giveBones.Click += (_, _) => ShowGiveBonesMenu(_giveBones);
		ToolTip.SetTip(_addPart, "A part (blank, or from a picture) on the picked bone");
		ToolTip.SetTip(_bind, "Puts parts on bones (they stay where they are): on the nearest bones, or see the ways to do it by hand");
		ToolTip.SetTip(DeleteButton, "Deletes the picked part, or the picked bone: handing what's on it to the bone above, or with everything on it. Undoable.");
		DeleteButton.Click += (_, _) => DeletePickedInRig(DeleteButton);
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
		WireRigBuilding();

		static UniformGrid Buttons(int columns, params Control[] buttons)
		{
			var grid = new UniformGrid { Columns = columns };
			foreach (var b in buttons)
			{
				b.Margin = new Thickness(0, 0, 3, 3);
				b.HorizontalAlignment = HorizontalAlignment.Stretch;
				if (b is Button button)
				{
					button.HorizontalContentAlignment = HorizontalAlignment.Center;
					button.Padding = new Thickness(2, 4);
				}
				grid.Children.Add(b);
			}
			return grid;
		}
		var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
		DockPanel.SetDock(SnapJointsBox, Dock.Right);
		header.Children.Add(SnapJointsBox);
		header.Children.Add(RigTitle);
		_buildButtons = Buttons(4, AddBoneButton, _addPart, _bind, DeleteButton);
		var views = Buttons(3, BonesView, LayersView, ColourView, PosesView, SkinsView, BoxesView);
		var top = new StackPanel
		{
			Margin = new Thickness(6, 0, 6, 4),
			Spacing = 2,
			Children = { header, KeepPartsBox, KeepAnimationsBox, _giveBones, _buildButtons, views, SetChips, RigHint }
		};
		_rigTools = top;
		var panel = new DockPanel();
		DockPanel.SetDock(top, Dock.Top);
		panel.Children.Add(top);
		_colourPanel = BuildColourPanel();
		_boxesPanel = BuildBoxesPanel();
		_posesPanel = BuildPosesTab();
		_skinsPanel = BuildSkinsTab();
		panel.Children.Add(new Grid { Margin = new Thickness(6, 0, 6, 6), Children = { RigList, _colourPanel, _boxesPanel, _posesPanel, _skinsPanel } });   // (one of them)
		return panel;
	}

	// Delete ▾: the picked part goes at once; for a bone, a menu (keep what's on it, or not)
	internal void DeletePickedInRig(Control under)
	{
		if (SceneLevel || State.AnimateMode) return;
		if (State.SelectedSlot is { } slot) _session.DeletePart(slot);
		else if (!IsDrawn && State.Selected is { } bone) DeleteBoneMenu(bone).ShowAt(under);
	}


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
				foreach (var obj in Scene.Objects.AsEnumerable().Reverse())   // (front first, like layers)
					items.Add(Row(ObjectRowText(obj), obj, 0, bold: true));
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
					items.Add(RigRow(Row("🦴 " + bone.Name, bone, depth, bold: false)));
					foreach (var slot in rig.Slots.Where(s => s.Bone == bone))
						items.Add(RigRow(Row("▣ " + slot.Name, slot, depth + 1, bold: false, part: true)));
					foreach (var child in bone.Children) AddBone(child, depth + 1);
				}
				foreach (var root in rig.Roots) AddBone(root, 0);
			}
			foreach (var item in items) item.ContextRequested += RowMenu;
			_shownPicks = new List<object>();   // (the new rows get their bands)
			RefreshSetChips();
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
		return RigRow(new ListBoxItem { Content = label, Tag = slot, Padding = new Thickness(8, 3) });
	}

	// What's over the list goes with what it shows, and with Build or Animate
	private void SyncRigTools()
	{
		bool inside = !SceneLevel;
		_rigSection.IsVisible = inside;
		_sceneSection.IsVisible = !inside;
		bool other = ShowingColour || ShowingBoxes || ShowingPoses || ShowingSkins;   // (a panel of its own instead of the list)
		BonesView.IsEnabled = !IsDrawn;
		BonesView.IsChecked = !ShowingLayers && !other;
		LayersView.IsChecked = ShowingLayers;
		ColourView.IsChecked = ShowingColour;
		PosesView.IsChecked = ShowingPoses;
		SkinsView.IsChecked = ShowingSkins;
		BoxesView.IsChecked = ShowingBoxes;
		RigList.IsVisible = !other;
		_colourPanel.IsVisible = ShowingColour;
		_boxesPanel.IsVisible = ShowingBoxes;
		_posesPanel.IsVisible = ShowingPoses;
		_skinsPanel.IsVisible = ShowingSkins;
		bool building = !State.AnimateMode;   // (Animate: what builds the rig is put away)
		RigTitle.Text = (IsDrawn ? "Parts" : "Rig") + (building ? "" : " · animating");
		_buildButtons.IsVisible = building;
		AddBoneButton.IsEnabled = _bind.IsEnabled = !IsDrawn;
		SetChips.IsVisible &= !other;
		if (ShowingColour) FillColourPanel();
		if (ShowingBoxes) SyncBoxesPanel(force: true);
		_giveBones.IsVisible = inside && IsDrawn && building;
		KeepPartsBox.IsVisible = SnapJointsBox.IsVisible = !IsDrawn && building;
		KeepAnimationsBox.IsVisible = building;
		_boneBuildButtons.IsVisible = _partDelete.IsVisible = building;
		KeepPartsBox.IsChecked = _tool.KeepPartsInPlace;
		KeepAnimationsBox.IsChecked = State.KeepAnimations;
		RigHint.Text = LayersHintText();
		RigHint.IsVisible = RigHint.Text.Length > 0;
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
		object? picked = SceneLevel ? null : (object?)State.SelectedSlot ?? State.Selected;
		SyncPickBands();
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
		if (RowAt(RigList, at)?.Tag is not { } pressed || pressed is not (Slot or Bone)) return;

		// The eye: hidden or shown; the lock: locked or not (neither picks the row)
		var source = e.Source as Visual;
		if (OnToggle(source, EyeMark) || OnToggle(source, LockMark))
		{
			e.Handled = true;
			if (OnToggle(source, EyeMark)) _session.ToggleHidden(pressed);
			else _session.ToggleLocked(pressed);
			return;
		}

		// Ctrl+click: picked or not; Shift+click: every row from the selected one to this one
		// (with Ctrl, added to what's picked)
		bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
		bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
		if (ctrl || shift)
		{
			e.Handled = true;   // (the list mustn't select it the usual way)
			if (shift) PickRows(pressed, keep: ctrl);
			else State.TogglePick(pressed);
			AfterPicking();
			return;
		}
		_rigPress = at;
		_rigDragged = pressed;
		_rigClicked = pressed;   // (a plain click: just this is picked, once it's let go of here)
	}

	private object? _rigClicked;

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
		var clicked = _rigClicked;
		_rigClicked = null;
		if (!dragging && clicked != null && RowAt(RigList, e.GetPosition(RigList))?.Tag == clicked)
		{
			State.Pick(new[] { clicked });
			AfterPicking();
			return;
		}
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
