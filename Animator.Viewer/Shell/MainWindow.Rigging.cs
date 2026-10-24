using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace Animator.Viewer;

// Rigging finished art: giving a drawn object bones, putting parts on bones (by hand, by dragging
// them in the list, or onto the nearest bones), and fitting bones without moving the parts
public partial class MainWindow
{
	// ======================= parts stay put =======================

	private void KeepParts_Click(object sender, RoutedEventArgs e)
	{
		_tool.KeepPartsInPlace = KeepPartsBox.IsChecked == true;
		_appSettings.KeepPartsInPlace = _tool.KeepPartsInPlace;
		_appSettings.Save();
	}

	// ======================= a drawn object gets bones =======================

	private void GiveBones_Click(object sender, RoutedEventArgs e)
	{
		if (!IsDrawn) return;
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };

		var root = new MenuItem { Header = "Just a root bone: I'll draw the rest (Ctrl+drag)" };
		root.Click += (_, _) => GiveBones(suggest: false);
		menu.Items.Add(root);

		var suggest = new MenuItem { Header = "Bones suggested from its parts (where they overlap)" };
		suggest.Click += (_, _) => GiveBones(suggest: true);
		suggest.IsEnabled = _skeleton.Slots.Count > 0;
		menu.Items.Add(suggest);

		menu.IsOpen = true;
	}

	// The drawn object becomes a rigged one. Its hidden bone becomes a root bone you can see, at the
	// object's origin, pointing up; with `suggest`, bones from its parts' shapes hang from it, each
	// part on its own. Every part stays exactly where it is.
	private void GiveBones(bool suggest)
	{
		if (_active is not { Kind: ObjectKind.Drawn } obj) return;
		ClosePaintEditor();
		int suggested = 0;
		bool done = EditStructureAndScene(() =>
		{
			obj.Kind = ObjectKind.Rigged;
			var root = _skeleton.Roots.FirstOrDefault() ?? RigOps.AddBone(_skeleton, null, "root", Vector2.Zero, 0f, 0f);
			var frozen = FrozenRest.Capture(_skeleton);
			float height = _renderer.ImageBounds(_skeleton) is { } box ? box.Height : 200f;
			root.Length = Math.Clamp(height * 0.12f, 30f, 200f);
			RestEdits.Turn(frozen, root, MathF.PI / 2);   // pointing up, the parts staying where they are
			if (suggest) suggested = PartRigging.SuggestBones(_skeleton, _assets, root);
			return suggest ? $"Give {obj.Name} suggested bones" : $"Give {obj.Name} a root bone";
		});
		if (!done) return;

		_state.Selected = _skeleton.Roots.FirstOrDefault();
		_state.SelectedSlot = null;
		_state.AnimateMode = false;
		RefreshEverything();
		ShowRigHint(suggest
			? $"{suggested} bone(s) suggested. Drag a joint to fit it (the parts stay put), drag parts onto bones in the list above, or Bind ▾."
			: "Ctrl+drag from the root bone to draw bones, then Bind ▾ puts every part on its nearest bone.");
	}

	// ======================= parts onto bones =======================

	private void Bind_Click(object sender, RoutedEventArgs e)
	{
		if (IsDrawn) return;
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		void Item(string header, bool enabled, Action click)
		{
			var item = new MenuItem { Header = header, IsEnabled = enabled };
			item.Click += (_, _) => click();
			menu.Items.Add(item);
		}

		bool bones = _skeleton.AllBones().Skip(1).Any();
		var roots = _skeleton.Roots.ToHashSet();
		var onRoots = _skeleton.Slots.Where(s => roots.Contains(s.Bone)).ToList();
		Item("Every part onto its nearest bone", bones && _skeleton.Slots.Count > 0,
			() => PartsToNearestBones(_skeleton.Slots.ToList(), "every part"));
		Item($"Parts still on the root bone onto their nearest bones ({onRoots.Count})", bones && onRoots.Count > 0,
			() => PartsToNearestBones(onRoots, "the parts on the root"));
		if (_state.SelectedSlot is { } selected)
			Item($"\"{selected.Name}\" onto its nearest bone", bones, () => PartsToNearestBones(new List<Slot> { selected }, selected.Name));

		menu.Items.Add(new Separator());
		string bone = _state.Selected is { } b && _state.SelectedSlot == null ? $"\"{b.Name}\"" : "the selected bone";
		Item($"By hand: Alt+click parts to put them on {bone}", false, () => { });
		Item("By hand: drag parts onto bones in the list", false, () => { });
		Item("By hand: a part's Bone in its properties", false, () => { });
		menu.IsOpen = true;
	}

	// Each part goes on the bone nearest to most of it (not a root bone, unless there's no other)
	private void PartsToNearestBones(List<Slot> slots, string what)
	{
		_state.EditStructure(() =>
		{
			int moved = 0;
			foreach (var slot in slots)
			{
				_skeleton.SetToSetupPose();
				_skeleton.UpdateWorld();
				if (PartRigging.NearestBone(_skeleton, slot, _assets) is { } bone && PartOps.MoveToBone(_skeleton, slot, bone)) moved++;
			}
			ShowRigHint(moved > 0 ? $"{moved} part(s) moved onto other bones." : "Every part already was on its nearest bone.");
			return moved > 0 ? $"Put {what} on the nearest bones" : null;
		});
	}

	// ======================= dragging in the list =======================

	// Drag a part onto a bone to put it there, or a bone onto another to hang it there (both stay
	// where they are)
	private Point? _treePress;
	private object? _treeDragged;   // the Slot or Bone being dragged

	private void BoneTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_treePress = e.GetPosition(BoneTree);
		_treeDragged = ItemUnder(e.OriginalSource as DependencyObject)?.Tag;
	}

	private void BoneTree_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed || _treePress is not { } press || _treeDragged == null || IsDrawn) return;
		var now = e.GetPosition(BoneTree);
		if (Math.Abs(now.X - press.X) < SystemParameters.MinimumHorizontalDragDistance &&
			Math.Abs(now.Y - press.Y) < SystemParameters.MinimumVerticalDragDistance)
			return;
		_treePress = null;
		DragDrop.DoDragDrop(BoneTree, new DataObject("HarlequinRig", _treeDragged), DragDropEffects.Move);
		_treeDragged = null;
	}

	private void BoneTree_DragOver(object sender, DragEventArgs e)
	{
		e.Effects = DropTarget(e) != null ? DragDropEffects.Move : DragDropEffects.None;
		e.Handled = true;
	}

	private void BoneTree_Drop(object sender, DragEventArgs e)
	{
		var dragged = e.Data.GetData("HarlequinRig");
		if (DropTarget(e) is not { } target) return;
		e.Handled = true;
		if (dragged is Slot slot)
		{
			_state.EditStructure(() =>
				PartOps.MoveToBone(_skeleton, slot, target) ? $"Put part {slot.Name} on {target.Name}" : null);
			_state.SelectedSlot = slot;
			_state.Selected = target;
		}
		else if (dragged is Bone bone)
		{
			_state.EditStructure(() =>
				RigOps.Reparent(_skeleton, bone, target) ? $"Hang {bone.Name} from {target.Name}" : null);
			_state.Selected = bone;
			_state.SelectedSlot = null;
		}
		RefreshRigPanel();
	}

	// The bone a drop would land on (a bone's row, or the bone of a part's row), if it means a change
	private Bone? DropTarget(DragEventArgs e)
	{
		if (IsDrawn || !e.Data.GetDataPresent("HarlequinRig")) return null;
		var dragged = e.Data.GetData("HarlequinRig");
		var bone = ItemUnder(e.OriginalSource as DependencyObject)?.Tag switch
		{
			Bone b => b,
			Slot s => s.Bone,
			_ => null
		};
		return dragged switch
		{
			Slot slot when bone != null && slot.Bone != bone => bone,
			Bone moving when bone != null && bone != moving && !bone.IsDescendantOf(moving) && moving.Parent != bone => bone,
			_ => null
		};
	}

	private static TreeViewItem? ItemUnder(DependencyObject? source)
	{
		while (source != null && source is not TreeViewItem)
			source = source is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(source)
				: LogicalTreeHelper.GetParent(source);
		return source as TreeViewItem;
	}
}
