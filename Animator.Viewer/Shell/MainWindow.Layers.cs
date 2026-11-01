using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Animator.Viewer;

// Layers: every part in the order it's drawn, whatever bone it's on, the way layers stack in any
// editor (a drawn object's list always shows them so; a rigged object's can show its bones or
// its layers). Drag a part up or down, or step it with Ctrl+] / Ctrl+[ (with Shift, all the way):
// in Setup mode that changes the rest order; in Animate mode it keys the order at the playhead.
public partial class MainWindow
{
	private bool _layersView;                                          // a rigged object's list shows its layers
	private IReadOnlyList<Slot> _listedOrder = Array.Empty<Slot>();    // the order the list was made in
	private bool _listedAnimating;                                     // ...and in which mode
	private TreeViewItem? _dropMark;                                   // the row a drop would land by

	private bool ShowingLayers => IsDrawn || _layersView;

	// The order the list shows and changes: the one the animation draws at the playhead (Animate
	// mode), or the rest order
	private IReadOnlyList<Slot> CurrentOrder()
	{
		if (!_state.AnimateMode) return _skeleton.Slots;
		_skeleton.ResetDrawOrder();
		_state.Animation.DrawOrder?.Apply(_skeleton, _state.Time);
		return _skeleton.DrawOrder;
	}

	private static int IndexIn(IReadOnlyList<Slot> order, Slot slot)
	{
		for (int i = 0; i < order.Count; i++)
			if (order[i] == slot) return i;
		return -1;
	}

	private void RigView_Changed(object sender, RoutedEventArgs e)
	{
		// (not while the window is still being built: the switch is set once the panel fills in)
		if (_syncingRig || !IsLoaded || BonesViewChoice == null || LayersViewChoice == null || ColourViewChoice == null || PosesViewChoice == null || SkinsViewChoice == null || BoxesViewChoice == null) return;
		_colourView = ColourViewChoice.IsChecked == true;
		_posesView = PosesViewChoice.IsChecked == true;
		_skinsView = SkinsViewChoice.IsChecked == true;
		_boxesView = BoxesViewChoice.IsChecked == true;
		if (!_colourView && !_posesView && !_skinsView && !_boxesView) _layersView = LayersViewChoice.IsChecked == true;
		RefreshRigPanel();
	}

	// The list's rows, front first; and what it says about the order
	private void BuildLayers()
	{
		var order = _listedOrder;
		for (int i = order.Count - 1; i >= 0; i--)
			BoneTree.Items.Add(MakeLayerItem(order[i]));
	}

	private TreeViewItem MakeLayerItem(Slot slot)
	{
		var header = new TextBlock();
		header.Inlines.Add(new Run($"▣ {slot.Name}"));
		if (!IsDrawn)
			header.Inlines.Add(new Run($"   on {slot.Bone.Name}") { Foreground = Themes.Brush("TextMuted"), FontStyle = FontStyles.Normal });
		if (slot.IsMask)
			header.Inlines.Add(new Run($"   ◐ mask{(slot.MaskShows ? ", shows" : "")}") { Foreground = new SolidColorBrush(Color.FromRgb(200, 150, 255)), FontStyle = FontStyles.Normal });
		else if (MaskOps.MaskOf(_skeleton, slot) is { } mask)
			header.Inlines.Add(new Run($"   ↳ clipped by {mask.Name}") { Foreground = new SolidColorBrush(Color.FromRgb(200, 150, 255)), FontStyle = FontStyles.Normal });
		if (SkinOps.SkinsWith(_skeleton, slot) is { Count: > 0 } skins)
			header.Inlines.Add(new Run($"   skin{(skins.Count == 1 ? "" : "s")}: {string.Join(", ", skins.Select(s => s.Name))}")
				{ Foreground = Themes.Brush("TextMuted"), FontStyle = FontStyles.Normal });
		var item = new TreeViewItem
		{
			Header = RowHeader(slot, header),
			Tag = slot,
			Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 110)),
			FontStyle = FontStyles.Italic,
			BorderBrush = Brushes.Transparent,
			ContextMenu = OrderMenu(slot)
		};
		_slotItems[slot] = item;
		return item;
	}

	// The switch between bones and layers, and the line under it
	private void SyncLayersView()
	{
		// A drawn object has no bones: its parts, or its colour layers
		BonesViewChoice.Visibility = IsDrawn ? Visibility.Collapsed : Visibility.Visible;
		RigViewSwitch.Columns = IsDrawn ? 5 : 6;
		bool other = _colourView || _posesView || _skinsView || _boxesView;
		BonesViewChoice.IsChecked = !other && !ShowingLayers;
		LayersViewChoice.IsChecked = !other && ShowingLayers;
		ColourViewChoice.IsChecked = _colourView;
		PosesViewChoice.IsChecked = _posesView;
		SkinsViewChoice.IsChecked = _skinsView;
		BoxesViewChoice.IsChecked = _boxesView;
		BoneTree.Visibility = other ? Visibility.Collapsed : Visibility.Visible;
		ColourPanel.Visibility = _colourView ? Visibility.Visible : Visibility.Collapsed;
		PosesPanel.Visibility = _posesView ? Visibility.Visible : Visibility.Collapsed;
		SkinsPanel.Visibility = _skinsView ? Visibility.Visible : Visibility.Collapsed;
		BoxesPanel.Visibility = _boxesView ? Visibility.Visible : Visibility.Collapsed;
		LayersHint.Text = LayersHintText();
		LayersHint.Visibility = LayersHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	// What the line under the switch says: what dragging does now
	private string LayersHintText()
	{
		if (!ShowingLayers || _skeleton.Slots.Count < 2) return "";
		if (_state.AnimateMode) return $"Animate: dragging a part up or down keys the order at frame {_state.Frame}.";
		return _skeleton.DrawOrder.SequenceEqual(_skeleton.Slots)
			? "Drag a part up or down to change the order (the top one is in front)."
			: "The rest order. This animation draws them in another order here: Animate (K) shows and keys that one.";
	}

	// Every frame: the list follows the order as it changes (playing past a key, switching mode),
	// and the line under the switch what dragging would do
	private void SyncLayerOrder()
	{
		if (_sceneLevel) return;
		var now = _state.AnimateMode ? _skeleton.DrawOrder : _skeleton.Slots;
		if (_listedAnimating != _state.AnimateMode || !now.SequenceEqual(_listedOrder)) RefreshRigPanel();
		else if (LayersHint.Text != LayersHintText()) SyncLayersView();
	}

	// ======================= changing the order =======================

	// Parts a step toward the front (+1) or the back (-1), or all the way
	private void Reorder(IReadOnlyCollection<Slot> parts, int direction, bool toEnd)
	{
		if (parts.Count == 0) return;
		var order = CurrentOrder().ToList();
		var moved = toEnd ? DrawOrderOps.ToEnd(order, parts, direction > 0) : DrawOrderOps.Stepped(order, parts, direction);
		string what = parts.Count == 1 ? parts.First().Name : $"{parts.Count} parts";
		string how = toEnd ? (direction > 0 ? "to the front" : "to the back") : (direction > 0 ? "forward" : "backward");
		ApplyOrder(moved, $"{what} {how}");
	}

	// The order becomes this one: the rest order, or (Animate mode) a key at the playhead
	private void ApplyOrder(IReadOnlyList<Slot> order, string name)
	{
		bool keyed = _state.AnimateMode;
		float time = _state.Time;
		var setup = _skeleton.Slots.ToList();
		_state.EditStructure(() =>
		{
			bool changed = keyed ? DrawOrderOps.Key(_state.Animation, time, order, setup) : DrawOrderOps.SetSetupOrder(_skeleton, order);
			return changed ? (keyed ? $"Key the draw order at frame {_state.Frame}: {name}" : $"Draw order: {name}") : null;
		});
		RefreshRigPanel();
	}

	// Right-click on a part: its place in the order
	private ContextMenu OrderMenu(Slot slot)
	{
		var menu = new ContextMenu();
		var items = new List<(MenuItem Item, int Direction)>();
		void Add(string header, string command, int direction, bool toEnd)
		{
			var item = new MenuItem { Header = header, Tag = command };
			item.Click += (_, _) =>
			{
				// A picked part takes the other picked parts along
				var parts = DraggedAlong(slot);
				if (parts.Count == 1)
				{
					_state.SelectedSlot = slot;
					_state.Selected = slot.Bone;
				}
				Reorder(parts, direction, toEnd);
			};
			menu.Items.Add(item);
			items.Add((item, direction));
		}
		Add("Bring to front", "order.front", +1, true);
		Add("Bring forward", "order.forward", +1, false);
		Add("Send backward", "order.backward", -1, false);
		Add("Send to back", "order.back", -1, true);
		menu.Items.Add(new Separator());
		var masking = new MenuItem();
		menu.Items.Add(masking);
		menu.Opened += (_, _) =>
		{
			FillMaskMenu(masking, slot);
			var order = CurrentOrder();
			var parts = DraggedAlong(slot);
			var at = parts.Select(part => IndexIn(order, part)).ToList();
			foreach (var (item, direction) in items)
			{
				item.InputGestureText = _keys.Gesture((string)item.Tag);
				item.IsEnabled = direction > 0 ? at.Min() < order.Count - parts.Count : at.Max() >= parts.Count;
				item.Header = parts.Count > 1 ? $"{BaseHeader((string)item.Tag)} ({parts.Count} parts)" : BaseHeader((string)item.Tag);
			}
		};
		return menu;
	}

	// ======================= clipping masks =======================

	// The part's clipping: make it a mask for the parts above it (all, or up to one), or change it
	private void FillMaskMenu(MenuItem menu, Slot slot)
	{
		menu.Items.Clear();
		var order = CurrentOrder();
		int at = IndexIn(order, slot);
		var above = order.Skip(at + 1).ToList();
		menu.Header = slot.IsMask ? $"Clipping mask ({MaskOps.Clipped(_skeleton, slot).Count} part(s))" : "Clip the parts above it";
		menu.IsEnabled = slot.IsMask || above.Count > 0;
		if (_state.AnimateMode)
		{
			menu.IsEnabled = false;
			menu.ToolTip = "Masks are part of the object: set them in Build";
			return;
		}

		void Item(string header, bool check, Func<string?> change)
		{
			var item = new MenuItem { Header = header, IsChecked = check };
			item.Click += (_, _) =>
			{
				_state.EditStructure(change);
				RefreshRigPanel();
			};
			menu.Items.Add(item);
		}
		Item($"All the parts above it ({above.Count})", slot.IsMask && slot.MaskUntil == null, () =>
		{
			MaskOps.MakeMask(_skeleton, slot, null, slot.MaskShows);
			return $"{slot.Name}: clip every part above it";
		});
		foreach (var until in above)
		{
			int count = IndexIn(order, until) - at;
			Item($"Up to {until.Name} ({count})", slot.IsMask && slot.MaskUntil == until, () =>
			{
				MaskOps.MakeMask(_skeleton, slot, until, slot.MaskShows);
				return $"{slot.Name}: clip the parts up to {until.Name}";
			});
		}
		if (!slot.IsMask) return;
		menu.Items.Add(new Separator());
		Item("The mask shows too", slot.MaskShows, () =>
		{
			slot.MaskShows = !slot.MaskShows;
			return slot.MaskShows ? $"{slot.Name}: show the mask too" : $"{slot.Name}: the mask doesn't show";
		});
		Item("Stop clipping", false, () =>
		{
			MaskOps.ClearMask(slot);
			return $"{slot.Name}: not a mask";
		});
	}

	// ======================= dragging in the layers =======================

	// Where a dragged part would land: in front of (above) or behind (below) the part under the mouse
	private (Slot Target, bool InFront, TreeViewItem Item)? LayerDrop(DragEventArgs e)
	{
		if (!ShowingLayers || e.Data.GetData("HarlequinRig") is not Slot dragged) return null;
		var item = ItemUnder(e.OriginalSource as DependencyObject);
		if (item?.Tag is not Slot target || DraggedAlong(dragged).Contains(target)) return null;
		return (target, e.GetPosition(item).Y < item.ActualHeight / 2, item);
	}

	// A line over (in front of) or under (behind) the row a drop would land by
	private void MarkDrop(TreeViewItem? item, bool inFront)
	{
		if (_dropMark != null && _dropMark != item)
		{
			_dropMark.BorderBrush = Brushes.Transparent;
			_dropMark.BorderThickness = new Thickness(0);
		}
		_dropMark = item;
		if (item == null) return;
		item.BorderBrush = new SolidColorBrush(Color.FromRgb(80, 220, 255));
		item.BorderThickness = inFront ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 2);
	}

	private void BoneTree_DragLeave(object sender, DragEventArgs e) => MarkDrop(null, false);

	// The dragged part (and the other picked parts, when it's one of them) in front of or behind
	// the target, keeping their own order
	private void DropLayer(Slot dragged, Slot target, bool inFront)
	{
		var moving = DraggedAlong(dragged);
		var order = CurrentOrder().ToList();
		var others = order.Where(s => !moving.Contains(s)).ToList();
		int index = others.IndexOf(target) + (inFront ? 1 : 0);
		string what = moving.Count == 1 ? dragged.Name : $"{moving.Count} parts";
		ApplyOrder(DrawOrderOps.Moved(order, moving, index), $"{what} {(inFront ? "in front of" : "behind")} {target.Name}");
		if (moving.Count == 1)
		{
			_state.SelectedSlot = dragged;
			_state.Selected = dragged.Bone;
		}
		RefreshRigPanel();
	}

	private static string BaseHeader(string command) => command switch
	{
		"order.front" => "Bring to front",
		"order.forward" => "Bring forward",
		"order.backward" => "Send backward",
		_ => "Send to back"
	};
}
