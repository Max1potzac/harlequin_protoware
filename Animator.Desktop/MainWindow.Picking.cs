using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;

namespace Animator.Desktop;

// Picking several bones and parts in the rig list (Ctrl+click: picked or not; Shift+click: every
// row from the selected one to this one; Ctrl+A: everything shown), hiding them while editing
// (the eye on each row; H hides the picked, Shift+H all the others, Alt+H shows everything
// again), locking them (the lock; L, Shift+L, Alt+L: a locked one can't be picked or dragged in
// the viewport), selection sets (the row of sets over the list: named groups picked in one
// click) and bone colours (the bone panel's Colour ▾). As the Windows editor has them
// (Picking, Sets); the edits are EditorSession's and undoable, and belong to the object.
public sealed partial class MainWindow
{
	private static readonly object EyeMark = new();   // the Tag of a row's eye
	private static readonly object LockMark = new();  // ...and of its lock
	private static readonly IBrush PickedRowBrush = new ImmutableSolidColorBrush(Color.FromArgb(70, 60, 140, 230));
	private static readonly IBrush LockOnBrush = new ImmutableSolidColorBrush(Color.FromRgb(240, 200, 120));
	private static readonly IBrush LockOffBrush = new ImmutableSolidColorBrush(Color.FromArgb(70, 150, 160, 176));
	private static readonly IBrush EyeOwnBrush = new ImmutableSolidColorBrush(Color.FromRgb(230, 230, 235));
	private static readonly IBrush EyeOnBrush = new ImmutableSolidColorBrush(Color.FromRgb(140, 146, 158));
	private static readonly IBrush EyeUnderBrush = new ImmutableSolidColorBrush(Color.FromRgb(90, 94, 104));
	private static readonly Geometry ShackleShape = Geometry.Parse("M4,5.5 V3.5 A2,2 0 0 1 8,3.5 V5.5");

	internal readonly WrapPanel SetChips = new() { Margin = new Thickness(0, 0, 0, 2) };
	private List<object> _shownPicks = new();   // what the rows show as picked

	// ======================= rows =======================

	// A bone's or part's row: its eye, its lock, a bone's colour (when it has one of its own),
	// then what it is (faded while hidden)
	private ListBoxItem RigRow(ListBoxItem item)
	{
		if (item.Content is not Control label || item.Tag is not { } what) return item;
		var (own, hidden) = what switch
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
			Foreground = own ? EyeOwnBrush : hidden ? EyeUnderBrush : EyeOnBrush,
			Background = Brushes.Transparent,   // (the whole width can be clicked)
			Cursor = new Cursor(StandardCursorType.Hand),
			VerticalAlignment = VerticalAlignment.Center
		};
		ToolTip.SetTip(eye, own ? "Hidden while you edit: click to show it again"
			: hidden ? "Hidden with the bone it's on: click to hide it too"
			: $"Shown: click to hide it while you edit ({_keys.Describe("pick.hide")}: hide the picked, {_keys.Describe("pick.hideOthers")}: all the others, {_keys.Describe("pick.showAll")}: show everything). Exports always show everything.");
		if (hidden) label.Opacity = 0.45;
		item.Content = null;   // (let go of it first: a control has one parent)
		var row = new StackPanel { Orientation = Orientation.Horizontal };
		row.Children.Add(eye);
		row.Children.Add(LockToggle(what));
		if (what is Bone { EditorColor: { } } coloured)
		{
			var c = SkeletonRenderer.BoneColor(coloured);
			var swatch = new TextBlock { Text = "■ ", Foreground = new ImmutableSolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue)) };
			ToolTip.SetTip(swatch, "This bone's colour in the viewport (Colour ▾ in the bone panel changes it)");
			row.Children.Add(swatch);
		}
		row.Children.Add(label);
		item.Content = row;
		return item;
	}

	// A padlock: bright and shut while locked, faint while not
	private Control LockToggle(object item)
	{
		bool locked = LockOps.IsLocked(item);
		var brush = locked ? LockOnBrush : LockOffBrush;
		var icon = new Canvas { Width = 12, Height = 12 };
		icon.Children.Add(new Path { Data = ShackleShape, Stroke = brush, StrokeThickness = 1.4 });
		var body = new Rectangle { Width = 8, Height = 6, Stroke = brush, StrokeThickness = 1, Fill = locked ? brush : Brushes.Transparent };
		Canvas.SetLeft(body, 2);
		Canvas.SetTop(body, 5);
		icon.Children.Add(body);
		var toggle = new Border
		{
			Child = icon,
			Width = 16,
			VerticalAlignment = VerticalAlignment.Center,
			Background = Brushes.Transparent,   // (the whole width can be clicked)
			Tag = LockMark,
			Cursor = new Cursor(StandardCursorType.Hand)
		};
		ToolTip.SetTip(toggle, locked
			? "Locked: it can't be picked or dragged in the viewport (clicks go through it). Click to unlock it."
			: $"Click to lock it: it can't be picked or dragged in the viewport, so you can work on what's around it ({_keys.Describe("pick.lock")}: lock the picked, {_keys.Describe("pick.lockOthers")}: all the others, {_keys.Describe("pick.unlockAll")}: unlock everything)");
		return toggle;
	}

	// What a row says (under its eye and lock)
	internal static TextBlock RowLabel(ListBoxItem item) => item.Content switch
	{
		TextBlock text => text,
		StackPanel row => row.Children.OfType<TextBlock>().Last(),
		_ => throw new InvalidOperationException("not a row")
	};

	// On a row's eye (EyeMark) or lock (LockMark)
	private static bool OnToggle(Visual? source, object mark)
	{
		for (var at = source; at != null && at is not ListBoxItem; at = at.GetVisualParent())
			if (at is Control { Tag: { } tag } && ReferenceEquals(tag, mark)) return true;
		return false;
	}

	// ======================= picking =======================

	// Every bone and part row, top to bottom
	private List<object> ListedRows() => _listed.Select(i => i.Tag).Where(t => t is Bone or Slot).Cast<object>().ToList();

	// Every row from the selected one to this one (keep: added to what's picked). The selected one
	// stays the one the panel shows, so the next Shift+click goes from it again.
	private void PickRows(object to, bool keep)
	{
		var rows = ListedRows();
		var from = State.Primary;
		int a = from != null ? rows.IndexOf(from) : -1, b = rows.IndexOf(to);
		if (b < 0) return;
		if (a < 0)
		{
			a = b;
			from = to;
		}
		var range = rows.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);
		State.Pick(keep ? State.Picked.Concat(range) : range, from);
	}

	// The list and the panel follow the picks
	private void AfterPicking()
	{
		_listedPick = new object();   // (picked again, whatever it was)
		SyncRigSelection();
		SyncPickBands(force: true);
		RefillProperties();
	}

	// The picked rows (other than the selected one, which the list shows itself) get a band
	private void SyncPickBands(bool force = false)
	{
		if (SceneLevel) return;
		var picked = State.Picked;
		if (!force && picked.SequenceEqual(_shownPicks)) return;
		_shownPicks = picked.ToList();
		var set = _shownPicks.ToHashSet();
		var primary = State.Primary;
		foreach (var row in _listed)
			row.Background = row.Tag is { } tag && set.Contains(tag) && !ReferenceEquals(tag, primary) ? PickedRowBrush : null;
	}

	// Every bone and part shown, and not locked (a drawn object's own bone isn't one to pick)
	internal void PickEverythingShown()
	{
		if (SceneLevel) return;
		var shown = new List<object>();
		var skeleton = State.Skeleton;
		if (!IsDrawn) shown.AddRange(skeleton.AllBones().Where(b => !b.IsHidden && !b.Locked));
		shown.AddRange(skeleton.Slots.Where(s => !s.IsHidden && !s.Locked));
		if (shown.Count == 0) return;
		var primary = State.Primary is { } current && shown.Contains(current) ? current : shown[0];
		State.Pick(shown, primary);
		AfterPicking();
	}

	// Every key of the picked bones and parts, selected in the timeline: dragged in the viewport,
	// they all move alike
	private void SelectKeysOfPicked()
	{
		if (SceneLevel) return;
		int count = _timeline.SelectKeysOf(State.PickedBones, State.PickedSlots);
		ShowRigHint(count > 0
			? $"{count} key(s) selected. Drag what's picked in the viewport (Animate), and they all move alike."
			: "What's picked has no keys in this animation.");
	}

	private void DefinePickCommands()
	{
		Add("pick.all", "Pick _everything shown", PickEverythingShown);
		Add("pick.keys", "Select the picked's _keys", SelectKeysOfPicked);
		Add("pick.hide", "_Hide the picked", () => { if (!SceneLevel) _session.HidePicked(); });
		Add("pick.hideOthers", "Hide everything _but the picked", () => { if (!SceneLevel) _session.HideAllButPicked(); });
		Add("pick.showAll", "_Show everything", () => { if (!SceneLevel) _session.ShowEverything(); });
		Add("pick.lock", "_Lock the picked", () => { if (!SceneLevel) _session.LockPicked(); });
		Add("pick.lockOthers", "Lock everything but the picked", () => { if (!SceneLevel) _session.LockAllButPicked(); });
		Add("pick.unlockAll", "_Unlock everything", () => { if (!SceneLevel) _session.UnlockEverything(); });
	}

	// ======================= selection sets =======================

	// The row of sets: one button each, then + Set
	private void RefreshSetChips()
	{
		SetChips.Children.Clear();
		SetChips.IsVisible = !SceneLevel;
		if (SceneLevel) return;
		var skeleton = State.Skeleton;
		foreach (var set in skeleton.Sets)
		{
			int count = set.MembersIn(skeleton).Count();
			var chip = new Button { Content = set.Name, Padding = new Thickness(6, 1), Margin = new Thickness(0, 0, 3, 3), Tag = set };
			ToolTip.SetTip(chip, $"{set.Name}: {count} bone(s) and part(s). Click: pick them. Ctrl+click: picked or not. Shift+click: pick them too. Right-click: change the set.");
			chip.Click += (_, _) => PickSet(set, _mods);
			chip.ContextRequested += (_, e) =>
			{
				e.Handled = true;
				SetMenu(set).ShowAt(chip, showAtPointer: true);
			};
			SetChips.Children.Add(chip);
		}
		var add = new Button { Content = "+ Set", Padding = new Thickness(6, 1), Margin = new Thickness(0, 0, 3, 3) };
		ToolTip.SetTip(add, "A set of the picked bones and parts, to pick them again in one click");
		add.Click += (_, _) => _ = NewSetFromPicked();
		SetChips.Children.Add(add);
	}

	// A set's members picked (what's hidden can't be): just them, or with Ctrl picked or not, or
	// with Shift picked too
	internal void PickSet(SelectionSet set, KeyMods mods)
	{
		var members = set.MembersIn(State.Skeleton).Where(m => !EditorSession.IsHiddenItem(m)).ToList();
		if (members.Count == 0)
		{
			ShowRigHint($"Nothing in {set.Name} to pick (gone, or hidden).");
			return;
		}
		if (mods.HasFlag(KeyMods.Control))
		{
			if (members.All(State.IsPicked)) State.Unpick(members);
			else State.PickMore(members);
		}
		else if (mods.HasFlag(KeyMods.Shift)) State.PickMore(members);
		else
		{
			var primary = State.Primary is { } current && members.Contains(current) ? current : members[0];
			State.Pick(members, primary);
		}
		AfterPicking();
	}

	internal MenuFlyout SetMenu(SelectionSet set)
	{
		var menu = new MenuFlyout();
		void Item(string header, bool enabled, Action click)
		{
			var item = new MenuItem { Header = header, IsEnabled = enabled };
			item.Click += (_, _) => click();
			menu.Items.Add(item);
		}
		var picked = State.Picked;
		Item($"Add the picked to {set.Name}", picked.Count > 0, () => _session.ChangeSet(set, add: true));
		Item($"Take the picked out of {set.Name}",
			picked.Any(p => p is Bone bone ? set.Bones.Contains(bone) : p is Slot slot && set.Slots.Contains(slot)),
			() => _session.ChangeSet(set, add: false));
		Item($"Just the picked in {set.Name}", picked.Count > 0, () => _session.ReplaceSet(set));
		menu.Items.Add(new Separator());
		Item("Rename…", true, () => _ = RenameSet(set));
		Item("Delete the set", true, () => _session.DeleteSet(set));
		return menu;
	}

	private string UniqueSetName(string baseName)
	{
		var used = State.Skeleton.Sets.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
			if (!used.Contains($"{baseName} {i}")) return $"{baseName} {i}";
	}

	// What a set's name is asked for with (the tests answer it); null: the user's answer
	internal Func<string, Task<string?>>? AnswerSetName { get; set; }

	internal async Task NewSetFromPicked()
	{
		var picked = State.Picked.ToList();
		if (picked.Count == 0)
		{
			ShowRigHint("Pick the bones and parts for the set first (Ctrl+click, Shift+click, or a box in the viewport), then + Set.");
			return;
		}
		string suggested = UniqueSetName(picked.Count == 1 ? EditorSession.NameOf(picked[0]) : "set");
		string? name = AnswerSetName is { } answer ? await answer(suggested) : await Dialogs.AskText(this, "New set",
			$"A name for the set of these {picked.Count} bone(s) and part(s):", suggested, n => _session.SetNameProblem(n, null));
		if (name != null) _session.NewSetFromPicked(name);
	}

	private async Task RenameSet(SelectionSet set)
	{
		string? name = await Dialogs.AskText(this, "Rename the set", "Its new name:", set.Name, n => _session.SetNameProblem(n, set));
		if (name != null) _session.RenameSet(set, name);
	}

	// ======================= bone colours =======================

	private static readonly (string Name, Vector3 Color)[] BonePalette =
	{
		("Red", new Vector3(0.92f, 0.33f, 0.33f)),
		("Orange", new Vector3(0.95f, 0.6f, 0.2f)),
		("Green", new Vector3(0.38f, 0.8f, 0.36f)),
		("Teal", new Vector3(0.25f, 0.8f, 0.75f)),
		("Blue", new Vector3(0.33f, 0.55f, 1f)),
		("Purple", new Vector3(0.68f, 0.45f, 1f)),
		("Pink", new Vector3(0.95f, 0.45f, 0.75f)),
		("Grey", new Vector3(0.65f, 0.67f, 0.7f)),
	};

	private static IBrush BrushOf(Vector3 c) => new ImmutableSolidColorBrush(Color.FromRgb((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255)));

	// The colour the picked bones are drawn in (all of them, or the selected one)
	internal MenuFlyout BoneColourMenu()
	{
		var bones = State.PickedBones.ToList();
		if (bones.Count == 0 && State.Selected is { } selected) bones.Add(selected);
		var menu = new MenuFlyout();
		void Add(string name, Vector3? color, IBrush swatch)
		{
			var item = new MenuItem
			{
				Header = name,
				Icon = new Border { Width = 14, Height = 10, Background = swatch, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) },
				ToggleType = MenuItemToggleType.CheckBox,
				IsChecked = bones.Count > 0 && bones.All(b => b.EditorColor == color),
				IsEnabled = bones.Count > 0
			};
			item.Click += (_, _) => _session.ColourBones(bones, color, name);
			menu.Items.Add(item);
		}
		var usual = SkeletonRenderer.DefaultBoneColor;
		Add("The usual yellow", null, new ImmutableSolidColorBrush(Color.FromRgb(usual.Red, usual.Green, usual.Blue)));
		foreach (var (name, color) in BonePalette) Add(name, color, BrushOf(color));
		menu.Items.Add(new Separator());
		var other = new MenuItem { Header = "Another colour…", IsEnabled = bones.Count > 0 };
		other.Click += async (_, _) =>
		{
			if (await ColourDialog.Ask(this, "Bone colour", SkeletonRenderer.BoneColor(bones[^1])) is not { } c) return;
			_session.ColourBones(bones, new Vector3(c.Red / 255f, c.Green / 255f, c.Blue / 255f), "a colour of their own");
		};
		menu.Items.Add(other);
		return menu;
	}
}
