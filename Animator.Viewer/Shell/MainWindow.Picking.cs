using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Animator.Viewer;

// Picking several bones and parts in the rig panel (Ctrl+click: picked or not, the other way
// round; Shift+click: every row from the selected one to this one; Ctrl+A: everything shown), and
// hiding them while editing (the eye on each row; H hides the picked, Shift+H all the others,
// Alt+H shows everything again). The viewport picks the same way (see BoneTool).
public partial class MainWindow
{
	private static readonly Brush PickedRowBrush = Frozen(Color.FromRgb(46, 72, 106));
	private static readonly Brush EyeOnBrush = Frozen(Color.FromRgb(150, 160, 176));
	private static readonly Brush EyeOffBrush = Frozen(Color.FromRgb(240, 240, 240));
	private static readonly Brush EyeUnderBrush = Frozen(Color.FromRgb(96, 102, 112));
	private static readonly object EyeMark = new();   // the Tag of a row's eye

	private static Brush Frozen(Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private List<object> _shownPicks = new();   // what the rows show as picked
	private object? _treeClicked;                // a plain click: when it's let go (not a drag), just this is picked

	// ======================= rows =======================

	// A row's header: its eye, then what the row is (faded while hidden)
	private StackPanel RowHeader(object item, TextBlock text)
	{
		var (own, hidden) = item switch
		{
			Bone bone => (bone.Hidden, bone.IsHidden),
			Slot slot => (slot.Hidden, slot.IsHidden),
			_ => (false, false)
		};
		var eye = new TextBlock
		{
			Text = hidden ? "○" : "●",
			Tag = EyeMark,
			Width = 16,
			FontStyle = FontStyles.Normal,
			Foreground = own ? EyeOffBrush : hidden ? EyeUnderBrush : EyeOnBrush,
			Background = Brushes.Transparent,   // the whole width can be clicked
			Cursor = Cursors.Hand,
			ToolTip = own ? "Hidden while you edit: click to show it again"
				: hidden ? "Hidden with the bone it's on: click to hide it too"
				: $"Shown: click to hide it while you edit ({KeyOf("pick.hide")}: hide the picked, {KeyOf("pick.hideOthers")}: all the others, {KeyOf("pick.showAll")}: show everything). Exports always show everything."
		};
		if (hidden) text.Opacity = 0.45;
		var row = new StackPanel { Orientation = Orientation.Horizontal };
		row.Children.Add(eye);
		row.Children.Add(text);
		return row;
	}

	private static DependencyObject? ParentOf(DependencyObject at) =>
		at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at);

	private static bool IsEye(DependencyObject? source)
	{
		for (var at = source; at != null && at is not TreeViewItem; at = ParentOf(at))
			if (at is FrameworkElement { Tag: { } tag } && ReferenceEquals(tag, EyeMark)) return true;
		return false;
	}

	// The little triangle that folds a row's children away
	private static bool OnExpander(DependencyObject? source)
	{
		for (var at = source; at != null && at is not TreeViewItem; at = ParentOf(at))
			if (at is ToggleButton) return true;
		return false;
	}

	// Every row there is to see, top to bottom (not those folded away)
	private List<object> ListedRows()
	{
		var rows = new List<object>();
		void Walk(ItemCollection items)
		{
			foreach (var row in items.OfType<TreeViewItem>())
			{
				if (row.Tag is Bone or Slot) rows.Add(row.Tag);
				if (row.IsExpanded) Walk(row.Items);
			}
		}
		Walk(BoneTree.Items);
		return rows;
	}

	// ======================= clicking rows =======================

	private void BoneTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		var source = e.OriginalSource as DependencyObject;
		var row = ItemUnder(source);
		_treePress = e.GetPosition(BoneTree);
		_treeDragged = row?.Tag;
		_treeClicked = null;
		if (row?.Tag is not { } item || item is not (Bone or Slot) || OnExpander(source)) return;

		// The eye: hidden or shown (that doesn't pick the row)
		if (IsEye(source))
		{
			e.Handled = true;
			_treeDragged = null;
			ToggleHidden(item);
			return;
		}

		var modifiers = Keyboard.Modifiers;
		bool ctrl = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift);
		if (ctrl || shift)
		{
			e.Handled = true;   // the tree mustn't select it the usual way
			_treeDragged = null;
			if (shift) PickRows(item, keep: ctrl);
			else _state.TogglePick(item);
			AfterPicking();
			return;
		}
		_treeClicked = item;   // (the tree selects it; picking just it waits for the release)
	}

	private void BoneTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (_treeClicked is not { } clicked) return;
		_treeClicked = null;
		if (ItemUnder(e.OriginalSource as DependencyObject)?.Tag != clicked) return;
		_state.Pick(new[] { clicked });
		AfterPicking();
	}

	// Every row from the selected one to this one (with Ctrl: added to what's picked). The selected
	// one stays the one the panel shows, so the next Shift+click goes from it again.
	private void PickRows(object to, bool keep)
	{
		var rows = ListedRows();
		var from = _state.Primary;
		int a = from != null ? rows.IndexOf(from) : -1, b = rows.IndexOf(to);
		if (b < 0) return;
		if (a < 0)
		{
			a = b;
			from = to;
		}
		var range = rows.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);
		_state.Pick(keep ? _state.Picked.Concat(range) : range, from);
	}

	// The tree, the panel and the status line follow the picks
	private void AfterPicking()
	{
		_syncingRig = true;
		SelectTreeItem();
		_syncingRig = false;
		SyncTreePicks(force: true);
		ShowProperties();
		UpdateStatus();
	}

	// The picked rows (other than the selected one, which the tree shows itself) get a band
	private void SyncTreePicks(bool force = false)
	{
		var picked = _state.Picked;
		if (!force && picked.SequenceEqual(_shownPicks)) return;
		_shownPicks = picked.ToList();
		var set = _shownPicks.ToHashSet();
		var primary = _state.Primary;
		foreach (var (bone, row) in _treeItems)
			row.Background = set.Contains(bone) && !ReferenceEquals(bone, primary) ? PickedRowBrush : Brushes.Transparent;
		foreach (var (slot, row) in _slotItems)
			row.Background = set.Contains(slot) && !ReferenceEquals(slot, primary) ? PickedRowBrush : Brushes.Transparent;
	}

	// What a drag in the list takes along: everything picked of its kind, when it's picked itself
	private List<T> DraggedAlong<T>(T dragged) where T : class
	{
		var picked = _state.Picked.OfType<T>().ToList();
		return picked.Count > 1 && picked.Contains(dragged) ? picked : new List<T> { dragged };
	}

	// ======================= picking everything, and their keys =======================

	// Every bone and part shown (a drawn object's own bone isn't one to pick)
	private void PickEverythingShown()
	{
		var shown = new List<object>();
		if (!IsDrawn) shown.AddRange(_skeleton.AllBones().Where(b => !b.IsHidden));
		shown.AddRange(_skeleton.Slots.Where(s => !s.IsHidden));
		if (shown.Count == 0) return;
		var primary = _state.Primary is { } current && shown.Contains(current) ? current : shown[0];
		_state.Pick(shown, primary);
		AfterPicking();
	}

	// Every key of the picked bones and parts, selected in the timeline: drag them in the
	// viewport, and the keys move alike (the animation plays as it did, moved)
	private void SelectKeysOfPicked()
	{
		int count = _timeline.SelectKeysOf(_state.PickedBones, _state.PickedSlots);
		ShowRigHint(count > 0
			? $"{count} key(s) selected. Drag what's picked in the viewport (Animate mode), and they all move alike."
			: "What's picked has no keys in this animation.");
	}

	// ======================= hiding =======================

	private static string NameOf(object item) => item switch { Bone b => b.Name, Slot s => s.Name, _ => "" };

	private static bool IsHiddenItem(object item) => item switch { Bone b => b.IsHidden, Slot s => s.IsHidden, _ => false };

	private void ToggleHidden(object item)
	{
		bool hide = item switch { Bone b => !b.Hidden, Slot s => !s.Hidden, _ => false };
		EditVisibility($"{(hide ? "Hide" : "Show")} {NameOf(item)}", () => VisibilityOps.SetHidden(new[] { item }, hide));
	}

	private void HidePicked()
	{
		var picked = _state.Picked.ToList();
		if (picked.Count == 0) return;
		EditVisibility(picked.Count == 1 ? $"Hide {NameOf(picked[0])}" : $"Hide {picked.Count} picked",
			() => VisibilityOps.SetHidden(picked, true));
	}

	private void HideAllButPicked()
	{
		var picked = _state.Picked.ToList();
		if (picked.Count == 0) return;
		EditVisibility("Hide everything but the picked", () => VisibilityOps.HideOthers(_skeleton, picked));
	}

	private void ShowEverything() => EditVisibility("Show everything", () => VisibilityOps.ShowAll(_skeleton));

	// One undo step (none when nothing changed). What's hidden can't stay picked: it couldn't be
	// seen being moved.
	private void EditVisibility(string name, Func<bool> change)
	{
		var before = VisibilitySnapshot.Capture(_skeleton);
		if (!change()) return;
		var hiddenPicks = _state.Picked.Where(IsHiddenItem).ToList();
		if (hiddenPicks.Count > 0) _state.Unpick(hiddenPicks);
		_state.Undo.Push(new SnapshotCommand<VisibilitySnapshot>(name, before, VisibilitySnapshot.Capture(_skeleton), s => s.Restore()));
		UpdateStatus();
	}
}
