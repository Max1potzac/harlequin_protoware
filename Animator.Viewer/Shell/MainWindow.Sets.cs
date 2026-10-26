using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Animator.Viewer;

// Bone colours (the colour button in the bone panel: for all the picked bones) and selection
// sets (the row of sets under the Bones / Layers switch): named groups of bones and parts, picked
// in one click (Ctrl+click: picked or not; Shift+click: picked too), changed from their own
// right-click menu. Both belong to the object and are saved with it.
public partial class MainWindow
{
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

	private static Brush BrushOf(Vector3 c) => Frozen(Color.FromRgb((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255)));

	private void BoneColor_Click(object sender, RoutedEventArgs e)
	{
		var bones = _state.PickedBones.ToList();
		if (bones.Count == 0 && _state.Selected is { } selected) bones.Add(selected);
		if (bones.Count == 0) return;

		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		void Add(string name, Vector3? color, Brush swatch)
		{
			var item = new MenuItem
			{
				Header = name,
				Icon = new Border { Width = 14, Height = 10, Background = swatch, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) },
				IsChecked = bones.All(b => b.EditorColor == color)
			};
			item.Click += (_, _) => ColourBones(bones, color, name);
			menu.Items.Add(item);
		}
		var usual = SkeletonRenderer.DefaultBoneColor;
		Add("The usual yellow", null, Frozen(Color.FromRgb(usual.Red, usual.Green, usual.Blue)));
		foreach (var (name, color) in BonePalette) Add(name, color, BrushOf(color));
		menu.Items.Add(new Separator());
		var other = new MenuItem { Header = "Another colour…" };
		other.Click += (_, _) =>
		{
			var start = SkeletonRenderer.BoneColor(bones[^1]);
			var dialog = new ColorPickerDialog(start, "Bone colour") { Owner = this };
			if (dialog.ShowDialog() != true) return;
			var c = dialog.Color;
			ColourBones(bones, new Vector3(c.Red / 255f, c.Green / 255f, c.Blue / 255f), "a colour of their own");
		};
		menu.Items.Add(other);
		menu.IsOpen = true;
	}

	private void ColourBones(List<Bone> bones, Vector3? color, string name)
	{
		string what = bones.Count == 1 ? bones[0].Name : $"{bones.Count} bones";
		EditAids($"{what}: {name.ToLowerInvariant()}", () =>
		{
			bool changed = false;
			foreach (var bone in bones)
			{
				if (bone.EditorColor == color) continue;
				bone.EditorColor = color;
				changed = true;
			}
			return changed;
		});
		ShowProperties();
	}

	// ======================= selection sets =======================

	// The row of sets: one button each, then + Set
	private void BuildSetChips()
	{
		SetChips.Children.Clear();
		foreach (var set in _skeleton.Sets)
		{
			int count = set.MembersIn(_skeleton).Count();
			var chip = new Button
			{
				Content = set.Name,
				Padding = new Thickness(6, 0, 6, 1),
				Margin = new Thickness(0, 0, 3, 3),
				Tag = set,
				ToolTip = $"{set.Name}: {count} bone(s) and part(s). Click: pick them. Ctrl+click: picked or not. Shift+click: pick them too. Right-click: change the set."
			};
			chip.Click += SetChip_Click;
			chip.ContextMenu = SetMenu(set);
			SetChips.Children.Add(chip);
		}
		var add = new Button
		{
			Content = "+ Set",
			Padding = new Thickness(6, 0, 6, 1),
			Margin = new Thickness(0, 0, 3, 3),
			ToolTip = "A set of the picked bones and parts, to pick them again in one click"
		};
		add.Click += (_, _) => NewSetFromPicked();
		SetChips.Children.Add(add);
		SetsRow.Visibility = Visibility.Visible;
	}

	private void SetChip_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: SelectionSet set }) return;
		// What's hidden can't be picked
		var members = set.MembersIn(_skeleton).Where(m => !IsHiddenItem(m)).ToList();
		if (members.Count == 0)
		{
			ShowRigHint($"Nothing in {set.Name} to pick (gone, or hidden).");
			return;
		}
		var modifiers = Keyboard.Modifiers;
		if (modifiers.HasFlag(ModifierKeys.Control))
		{
			if (members.All(_state.IsPicked)) _state.Unpick(members);
			else _state.PickMore(members);
		}
		else if (modifiers.HasFlag(ModifierKeys.Shift)) _state.PickMore(members);
		else
		{
			var primary = _state.Primary is { } current && members.Contains(current) ? current : members[0];
			_state.Pick(members, primary);
		}
		AfterPicking();
	}

	private ContextMenu SetMenu(SelectionSet set)
	{
		var menu = new ContextMenu();
		void Item(string header, Action click, Func<bool>? enabled = null)
		{
			var item = new MenuItem { Header = header };
			item.Click += (_, _) => click();
			if (enabled != null) menu.Opened += (_, _) => item.IsEnabled = enabled();
			menu.Items.Add(item);
		}
		Item($"Add the picked to {set.Name}", () => ChangeSet(set, add: true), () => _state.Picked.Count > 0);
		Item($"Take the picked out of {set.Name}", () => ChangeSet(set, add: false),
			() => _state.Picked.Any(p => p is Bone bone ? set.Bones.Contains(bone) : p is Slot slot && set.Slots.Contains(slot)));
		Item($"Just the picked in {set.Name}", () => ReplaceSet(set), () => _state.Picked.Count > 0);
		menu.Items.Add(new Separator());
		Item("Rename…", () => RenameSet(set));
		Item("Delete the set", () => EditSets($"Delete the set {set.Name}", () => _skeleton.Sets.Remove(set)));
		return menu;
	}

	private string UniqueSetName(string baseName)
	{
		var used = _skeleton.Sets.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
			if (!used.Contains($"{baseName} {i}")) return $"{baseName} {i}";
	}

	private string? SetNameProblem(string name, SelectionSet? renaming) =>
		name.Length == 0 ? "A set needs a name."
		: _skeleton.Sets.Any(s => s != renaming && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ? "Another set has that name."
		: null;

	private void NewSetFromPicked()
	{
		var picked = _state.Picked.ToList();
		if (picked.Count == 0)
		{
			ShowRigHint("Pick the bones and parts for the set first (Ctrl+click, Shift+click, or a box in the viewport), then + Set.");
			return;
		}
		var dialog = new InputDialog("New set", $"A name for the set of these {picked.Count} bone(s) and part(s):",
			UniqueSetName(picked.Count == 1 ? NameOf(picked[0]) : "set"), name => SetNameProblem(name, null)) { Owner = this };
		if (dialog.ShowDialog() != true) return;
		var set = new SelectionSet { Name = dialog.Answer };
		set.Bones.AddRange(picked.OfType<Bone>());
		set.Slots.AddRange(picked.OfType<Slot>());
		EditSets($"New set {set.Name}", () =>
		{
			_skeleton.Sets.Add(set);
			return true;
		});
	}

	private void ChangeSet(SelectionSet set, bool add)
	{
		var picked = _state.Picked.ToList();
		EditSets(add ? $"Add {picked.Count} to the set {set.Name}" : $"Take {picked.Count} out of the set {set.Name}", () =>
		{
			bool changed = false;
			foreach (var bone in picked.OfType<Bone>())
				changed |= add ? AddOnce(set.Bones, bone) : set.Bones.Remove(bone);
			foreach (var slot in picked.OfType<Slot>())
				changed |= add ? AddOnce(set.Slots, slot) : set.Slots.Remove(slot);
			return changed;
		});
	}

	private static bool AddOnce<T>(List<T> list, T item)
	{
		if (list.Contains(item)) return false;
		list.Add(item);
		return true;
	}

	private void ReplaceSet(SelectionSet set)
	{
		var picked = _state.Picked.ToList();
		EditSets($"The set {set.Name}: just the picked", () =>
		{
			set.Bones.Clear();
			set.Bones.AddRange(picked.OfType<Bone>());
			set.Slots.Clear();
			set.Slots.AddRange(picked.OfType<Slot>());
			return true;
		});
	}

	private void RenameSet(SelectionSet set)
	{
		var dialog = new InputDialog("Rename the set", "Its new name:", set.Name, name => SetNameProblem(name, set)) { Owner = this };
		if (dialog.ShowDialog() != true || dialog.Answer == set.Name) return;
		string old = set.Name;
		EditSets($"Rename the set {old} to {dialog.Answer}", () =>
		{
			set.Name = dialog.Answer;
			return true;
		});
	}

	// One undo step; the row shows it (Undo.Changed rebuilds the rig panel)
	private void EditSets(string name, Func<bool> change)
	{
		var before = SetsSnapshot.Capture(_skeleton);
		if (!change()) return;
		_state.Undo.Push(new SnapshotCommand<SetsSnapshot>(name, before, SetsSnapshot.Capture(_skeleton), s => s.Restore()));
	}
}
