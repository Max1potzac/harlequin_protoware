using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Animator.Desktop;

// The list on the left: inside an object, its bones (as a tree, by indent) with the parts on
// each; at scene level, the scene's objects. Picking one there picks it in the viewport, and
// the other way round; a double-click on an object opens it.
public sealed partial class MainWindow
{
	internal readonly ListBox RigList = new();
	private bool _syncingRig;

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
		return RigList;
	}

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

	// The list made again (another object open, a bone added or taken away…)
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
			else
			{
				var rig = State.Skeleton;
				void AddBone(Bone bone, int depth)
				{
					if (_session.Active?.Kind != ObjectKind.Drawn) items.Add(Row("🦴 " + bone.Name, bone, depth, bold: false));
					foreach (var slot in rig.Slots.Where(s => s.Bone == bone))
						items.Add(Row("▣ " + slot.Name, slot, depth + 1, bold: false, part: true));
					foreach (var child in bone.Children) AddBone(child, depth + 1);
				}
				foreach (var root in rig.Roots) AddBone(root, 0);
			}
			RigList.ItemsSource = items;
			_listed = items;
			_listedPick = null;   // (picked again in the new rows, below)
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

	private static ListBoxItem Row(string text, object tag, int depth, bool bold, bool part = false)
	{
		var label = new TextBlock { Text = text, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
		if (part) label.Foreground = PartColour;   // (bones keep the theme's colour)
		return new ListBoxItem { Content = label, Tag = tag, Padding = new Thickness(8 + depth * 14, 3, 8, 3) };
	}

	// Every frame: what's picked in the viewport is picked in the list
	private void SyncRigSelection()
	{
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
}
