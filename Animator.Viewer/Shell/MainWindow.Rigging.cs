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

		var root = new MenuItem { Header = $"Just a root bone: I'll draw the rest (Ctrl+{MoveDrag})" };
		root.Click += (_, _) => GiveBones(suggest: false);
		menu.Items.Add(root);

		var suggest = new MenuItem { Header = "Bones suggested from its parts (where they overlap)" };
		suggest.Click += (_, _) => GiveBones(suggest: true);
		suggest.IsEnabled = _skeleton.Slots.Count > 0;
		menu.Items.Add(suggest);

		menu.IsOpen = true;
	}

	// The drawn object becomes a rigged one (see PartRigging.GiveBones): every part stays exactly
	// where it is
	private void GiveBones(bool suggest)
	{
		if (_active is not { Kind: ObjectKind.Drawn } obj) return;
		ClosePaintEditor();
		_skeleton.SetToSetupPose();
		_skeleton.UpdateWorld();
		float height = _renderer.ImageBounds(_skeleton) is { } box ? box.Height : 200f;
		int suggested = 0;
		bool done = EditStructureAndScene(() =>
		{
			suggested = PartRigging.GiveBones(obj, _assets, Math.Clamp(height * 0.12f, 30f, 200f), suggest);
			return suggest ? $"Give {obj.Name} suggested bones" : $"Give {obj.Name} a root bone";
		});
		if (!done) return;

		_state.Selected = _skeleton.Roots.FirstOrDefault();
		_state.SelectedSlot = null;
		_state.AnimateMode = false;
		RefreshEverything();
		ShowRigHint(suggest
			? $"{suggested} bone(s) suggested. {Capitalized(MoveDrag)} a joint to fit it (the parts stay put), drag parts onto bones in the list above, or Bind ▾."
			: $"Ctrl+{MoveDrag} from the root bone to draw bones, then Bind ▾ puts every part on its nearest bone.");
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
		Item($"By hand: Alt+{ButtonName(_tool.TransformButton).ToLowerInvariant()}-click parts to put them on {bone}", false, () => { });
		Item("By hand: drag parts onto bones in the list", false, () => { });
		Item("By hand: a part's Bone in its properties", false, () => { });
		menu.IsOpen = true;
	}

	// Each part goes on the bone nearest to most of it (not a root bone, unless there's no other)
	private void PartsToNearestBones(List<Slot> slots, string what)
	{
		_state.EditStructure(() =>
		{
			int moved = PartRigging.PartsToNearestBones(_skeleton, _assets, slots);
			ShowRigHint(moved > 0 ? $"{moved} part(s) moved onto other bones." : "Every part already was on its nearest bone.");
			return moved > 0 ? $"Put {what} on the nearest bones" : null;
		});
	}

	// Hangs a bone from another (null = a root bone). With "parts stay put", one on its parent's end
	// lets go first, so it and what's on it stay where they are; otherwise it moves onto the new
	// parent's end. (Inside EditStructure: if it can't, the snapshot puts the joint back.)
	private bool HangBone(Bone bone, Bone? parent)
	{
		if (_tool.KeepPartsInPlace && parent != bone.Parent) RigOps.SetConnected(bone, false);
		return RigOps.Reparent(_skeleton, bone, parent);
	}

	// ======================= dragging in the list =======================

	// Drag a part onto a bone to put it there, or a bone onto another to hang it there (both stay
	// where they are). A picked one takes the others picked along. (Clicks: MainWindow.Picking.cs)
	private Point? _treePress;
	private object? _treeDragged;   // the Slot or Bone being dragged

	private void BoneTree_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed || _treePress is not { } press || _treeDragged == null) return;
		if (ShowingLayers ? _treeDragged is not Slot : IsDrawn) return;   // layers: parts move up and down
		var now = e.GetPosition(BoneTree);
		if (Math.Abs(now.X - press.X) < SystemParameters.MinimumHorizontalDragDistance &&
			Math.Abs(now.Y - press.Y) < SystemParameters.MinimumVerticalDragDistance)
			return;
		_treePress = null;
		_treeClicked = null;   // a drag, not a click: what's picked stays picked
		DragDrop.DoDragDrop(BoneTree, new DataObject("HarlequinRig", _treeDragged), DragDropEffects.Move);
		_treeDragged = null;
	}

	private void BoneTree_DragOver(object sender, DragEventArgs e)
	{
		if (ShowingLayers)
		{
			var drop = LayerDrop(e);
			MarkDrop(drop?.Item, drop?.InFront ?? false);
			e.Effects = drop != null ? DragDropEffects.Move : DragDropEffects.None;
		}
		else e.Effects = DropTarget(e) != null ? DragDropEffects.Move : DragDropEffects.None;
		e.Handled = true;
	}

	private void BoneTree_Drop(object sender, DragEventArgs e)
	{
		if (ShowingLayers)
		{
			var drop = LayerDrop(e);
			MarkDrop(null, false);
			if (drop is { } layer && e.Data.GetData("HarlequinRig") is Slot part)
			{
				e.Handled = true;
				DropLayer(part, layer.Target, layer.InFront);
			}
			return;
		}
		var dragged = e.Data.GetData("HarlequinRig");
		if (DropTarget(e) is not { } target) return;
		e.Handled = true;
		if (dragged is Slot slot)
		{
			var parts = DraggedAlong(slot).Where(s => s.Bone != target).ToList();
			_state.EditStructure(() =>
			{
				int moved = parts.Count(part => PartOps.MoveToBone(_skeleton, part, target));
				return moved == 0 ? null : moved == 1 && parts.Count == 1 ? $"Put part {slot.Name} on {target.Name}" : $"Put {moved} parts on {target.Name}";
			});
			if (parts.Count == 1)
			{
				_state.SelectedSlot = slot;
				_state.Selected = target;
			}
		}
		else if (dragged is Bone bone)
		{
			// The picked bones no other dragged bone is above (those come along anyway), and not
			// the one they'd hang from or anything above it
			var along = DraggedAlong(bone);
			var bones = RestEdits.TopOf(along).Where(b => b != target && !target.IsDescendantOf(b) && b.Parent != target).ToList();
			if (bones.Count == 0) bones.Add(bone);   // (the drop target checked this one can go there)
			_state.EditStructure(() =>
			{
				int hung = bones.Count(b => HangBone(b, target));
				return hung == 0 ? null : bones.Count == 1 ? $"Hang {bones[0].Name} from {target.Name}" : $"Hang {hung} bones from {target.Name}";
			});
			if (bones.Count == 1)
			{
				_state.Selected = bone;
				_state.SelectedSlot = null;
			}
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
