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
		if (_syncingRig || !IsLoaded || BonesViewChoice == null || LayersViewChoice == null) return;
		_layersView = LayersViewChoice.IsChecked == true;
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
			header.Inlines.Add(new Run($"   on {slot.Bone.Name}") { Foreground = new SolidColorBrush(Color.FromRgb(140, 150, 165)), FontStyle = FontStyles.Normal });
		var item = new TreeViewItem
		{
			Header = header,
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
		RigViewSwitch.Visibility = IsDrawn ? Visibility.Collapsed : Visibility.Visible;
		BonesViewChoice.IsChecked = !_layersView;
		LayersViewChoice.IsChecked = _layersView;
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
			: "The rest order. This animation draws them in another order here: Animate mode (K) shows and keys that one.";
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
		void Add(string header, string keys, int direction, bool toEnd)
		{
			var item = new MenuItem { Header = header, InputGestureText = keys };
			item.Click += (_, _) =>
			{
				_state.SelectedSlot = slot;
				_state.Selected = slot.Bone;
				Reorder(new[] { slot }, direction, toEnd);
			};
			menu.Items.Add(item);
			items.Add((item, direction));
		}
		Add("Bring to front", "Ctrl+Shift+]", +1, true);
		Add("Bring forward", "Ctrl+]", +1, false);
		Add("Send backward", "Ctrl+[", -1, false);
		Add("Send to back", "Ctrl+Shift+[", -1, true);
		menu.Opened += (_, _) =>
		{
			var order = CurrentOrder();
			int at = IndexIn(order, slot);
			foreach (var (item, direction) in items)
				item.IsEnabled = direction > 0 ? at < order.Count - 1 : at > 0;
		};
		return menu;
	}

	// ======================= dragging in the layers =======================

	// Where a dragged part would land: in front of (above) or behind (below) the part under the mouse
	private (Slot Target, bool InFront, TreeViewItem Item)? LayerDrop(DragEventArgs e)
	{
		if (!ShowingLayers || e.Data.GetData("HarlequinRig") is not Slot dragged) return null;
		var item = ItemUnder(e.OriginalSource as DependencyObject);
		if (item?.Tag is not Slot target || target == dragged) return null;
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

	private void DropLayer(Slot dragged, Slot target, bool inFront)
	{
		var order = CurrentOrder().ToList();
		var others = order.Where(s => s != dragged).ToList();
		int index = others.IndexOf(target) + (inFront ? 1 : 0);
		ApplyOrder(DrawOrderOps.Moved(order, new[] { dragged }, index), $"{dragged.Name} {(inFront ? "in front of" : "behind")} {target.Name}");
		_state.SelectedSlot = dragged;
		_state.Selected = dragged.Bone;
		RefreshRigPanel();
	}
}
