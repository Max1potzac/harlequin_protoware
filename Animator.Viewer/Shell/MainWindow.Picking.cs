using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Animator.Viewer;

// Picking several bones and parts in the rig panel (Ctrl+click: picked or not, the other way
// round; Shift+click: every row from the selected one to this one; Ctrl+A: everything shown),
// hiding them while editing (the eye on each row; H hides the picked, Shift+H all the others,
// Alt+H shows everything again), and locking them (the lock; L, Shift+L, Alt+L): a locked one
// can't be picked or dragged in the viewport. The viewport picks the same way (see BoneTool).
public partial class MainWindow
{
	private static Brush PickedRowBrush => Themes.Brush("Selection");
	private static Brush EyeOnBrush => Themes.Brush("TextMuted");
	private static Brush EyeOffBrush => Themes.Brush("Text");
	private static Brush EyeUnderBrush => Themes.Brush("TextDisabled");
	private static readonly object EyeMark = new();   // the Tag of a row's eye
	private static readonly Brush LockOnBrush = Frozen(Color.FromRgb(240, 200, 120));
	private static readonly Brush LockOffBrush = Frozen(Color.FromArgb(70, 150, 160, 176));
	private static readonly object LockMark = new();  // ...and of its lock
	private static readonly Geometry ShackleShape = Freeze(Geometry.Parse("M4,5.5 V3.5 A2,2 0 0 1 8,3.5 V5.5"));

	private static Geometry Freeze(Geometry geometry)
	{
		geometry.Freeze();
		return geometry;
	}

	private static Brush Frozen(Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private List<object> _shownPicks = new();   // what the rows show as picked
	private object? _treeClicked;                // a plain click: when it's let go (not a drag), just this is picked

	// ======================= rows =======================

	// A row's header: its eye, its lock, a bone's colour (when it has one of its own), then what
	// the row is (faded while hidden)
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
		row.Children.Add(LockToggle(item));
		if (item is Bone { EditorColor: { } } coloured)
		{
			var c = SkeletonRenderer.BoneColor(coloured);
			row.Children.Add(new TextBlock
			{
				Text = "■ ",
				FontStyle = FontStyles.Normal,
				Foreground = Frozen(Color.FromRgb(c.Red, c.Green, c.Blue)),
				ToolTip = "This bone's colour in the viewport (the bone panel's colour button changes it)"
			});
		}
		row.Children.Add(text);
		return row;
	}

	// A padlock: bright and shut while locked, faint while not
	private FrameworkElement LockToggle(object item)
	{
		bool locked = LockOps.IsLocked(item);
		var brush = locked ? LockOnBrush : LockOffBrush;
		var icon = new Grid { Width = 12, Height = 12 };
		icon.Children.Add(new System.Windows.Shapes.Path { Data = ShackleShape, Stroke = brush, StrokeThickness = 1.4 });
		icon.Children.Add(new System.Windows.Shapes.Rectangle
		{
			Width = 8,
			Height = 6,
			Margin = new Thickness(2, 5, 0, 0),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Stroke = brush,
			StrokeThickness = 1,
			Fill = locked ? brush : Brushes.Transparent
		});
		return new Border
		{
			Child = icon,
			Width = 16,
			VerticalAlignment = VerticalAlignment.Center,
			Background = Brushes.Transparent,   // the whole width can be clicked
			Tag = LockMark,
			Cursor = Cursors.Hand,
			ToolTip = locked
				? "Locked: it can't be picked or dragged in the viewport (clicks go through it). Click to unlock it."
				: $"Click to lock it: it can't be picked or dragged in the viewport, so you can work on what's around it ({KeyOf("pick.lock")}: lock the picked, {KeyOf("pick.lockOthers")}: all the others, {KeyOf("pick.unlockAll")}: unlock everything)"
		};
	}

	private static DependencyObject? ParentOf(DependencyObject at) =>
		at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at);

	// On a row's eye (EyeMark) or lock (LockMark)
	private static bool OnToggle(DependencyObject? source, object mark)
	{
		for (var at = source; at != null && at is not TreeViewItem; at = ParentOf(at))
			if (at is FrameworkElement { Tag: { } tag } && ReferenceEquals(tag, mark)) return true;
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

		// The eye: hidden or shown; the lock: locked or not (neither picks the row)
		if (OnToggle(source, EyeMark) || OnToggle(source, LockMark))
		{
			e.Handled = true;
			_treeDragged = null;
			if (OnToggle(source, EyeMark)) ToggleHidden(item);
			else ToggleLocked(item);
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

	// Every bone and part shown, and not locked (a drawn object's own bone isn't one to pick).
	// Parts that show nothing at this frame are picked too: they move with the others.
	private void PickEverythingShown()
	{
		var shown = new List<object>();
		if (!IsDrawn) shown.AddRange(_skeleton.AllBones().Where(b => !b.IsHidden && !b.Locked));
		shown.AddRange(_skeleton.Slots.Where(s => !s.IsHidden && !s.Locked));
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
			? $"{count} key(s) selected. Drag what's picked in the viewport (Animate), and they all move alike."
			: "What's picked has no keys in this animation.");
	}

	// ======================= hiding =======================

	private static string NameOf(object item) => item switch { Bone b => b.Name, Slot s => s.Name, _ => "" };

	private static bool IsHiddenItem(object item) => item switch { Bone b => b.IsHidden, Slot s => s.IsHidden, _ => false };

	private void ToggleHidden(object item)
	{
		bool hide = item switch { Bone b => !b.Hidden, Slot s => !s.Hidden, _ => false };
		EditAids($"{(hide ? "Hide" : "Show")} {NameOf(item)}", () => VisibilityOps.SetHidden(new[] { item }, hide));
	}

	private void HidePicked()
	{
		var picked = _state.Picked.ToList();
		if (picked.Count == 0) return;
		EditAids(picked.Count == 1 ? $"Hide {NameOf(picked[0])}" : $"Hide {picked.Count} picked",
			() => VisibilityOps.SetHidden(picked, true));
	}

	private void HideAllButPicked()
	{
		var picked = _state.Picked.ToList();
		if (picked.Count == 0) return;
		EditAids("Hide everything but the picked", () => VisibilityOps.HideOthers(_skeleton, picked));
	}

	private void ShowEverything() => EditAids("Show everything", () => VisibilityOps.ShowAll(_skeleton));

	// ======================= locking =======================

	// (a locked one picked here in the list stays picked: its numbers can still be changed)
	private void ToggleLocked(object item)
	{
		bool locking = !LockOps.IsLocked(item);
		EditAids($"{(locking ? "Lock" : "Unlock")} {NameOf(item)}", () => LockOps.SetLocked(new[] { item }, locking));
	}

	// The picked locked, and let go: they can't be dragged any more
	private void LockPicked()
	{
		var picked = _state.Picked.ToList();
		if (picked.Count == 0) return;
		EditAids(picked.Count == 1 ? $"Lock {NameOf(picked[0])}" : $"Lock {picked.Count} picked",
			() => LockOps.SetLocked(picked, true), letLockedGo: true);
	}

	// Everything but the picked locked: clicks only reach the picked ones
	private void LockAllButPicked()
	{
		var picked = _state.Picked.ToList();
		if (picked.Count == 0) return;
		EditAids("Lock everything but the picked", () => LockOps.LockOthers(_skeleton, picked));
	}

	private void UnlockEverything() => EditAids("Unlock everything", () => LockOps.UnlockAll(_skeleton));

	// One undo step (none when nothing changed). What's hidden can't stay picked: it couldn't be
	// seen being moved; nor can what's just been locked with L.
	private void EditAids(string name, Func<bool> change, bool letLockedGo = false)
	{
		var before = EditAidsSnapshot.Capture(_skeleton);
		if (!change()) return;
		var gone = _state.Picked.Where(i => IsHiddenItem(i) || (letLockedGo && LockOps.IsLocked(i))).ToList();
		if (gone.Count > 0) _state.Unpick(gone);
		_state.Undo.Push(new SnapshotCommand<EditAidsSnapshot>(name, before, EditAidsSnapshot.Capture(_skeleton), s => s.Restore()));
		UpdateStatus();
	}
}
