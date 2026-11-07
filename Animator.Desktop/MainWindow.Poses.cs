using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The Poses tab: poses kept by name and put on again (partly, on just the picked, or mirrored
// onto the other side), the pose mirrored, copied and pasted (mirrored too), and the rig
// symmetrized. Sides come from names (arm_L / arm_R, Left Arm / Right Arm…). In Animate they key
// the pose at the playhead (auto key off: held until S); in Build they change the rest pose.
public sealed partial class MainWindow
{
	internal readonly ListBox PoseList = new() { MinHeight = 120, MaxHeight = 260 };
	internal readonly TextBox PoseName = new();
	internal readonly Slider PoseAmount = new() { Minimum = 0, Maximum = 100, Value = 100 };
	internal readonly CheckBox PoseOnlyPicked = new() { Content = "Only on the picked" };
	private readonly TextBlock _poseAmountText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 40 };
	private readonly TextBlock _poseHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) };
	private readonly List<Control> _needsPose = new();
	private readonly List<Control> _needsClipboard = new();
	private Button _symmetrize = null!;
	internal SavedPose? PickedPose { get; private set; }
	private bool _syncingPoses;

	private Control BuildPosesTab()
	{
		Button B(string text, Action click, List<Control>? needs = null)
		{
			var b = new Button { Content = text };
			b.Click += (_, _) => click();
			needs?.Add(b);
			return b;
		}
		static WrapPanel Row(params Control[] controls)
		{
			var row = new WrapPanel();
			foreach (var c in controls) { c.Margin = new Thickness(0, 0, 4, 4); row.Children.Add(c); }
			return row;
		}

		PoseAmount.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) _poseAmountText.Text = $"{PoseAmount.Value:0}%"; };
		_poseAmountText.Text = "100%";
		var amount = new DockPanel();
		DockPanel.SetDock(_poseAmountText, Dock.Right);
		amount.Children.Add(_poseAmountText);
		amount.Children.Add(PoseAmount);

		_symmetrize = B("Symmetrize the rig", Symmetrize);
		var panel = new StackPanel { Margin = new Thickness(6), Spacing = 4 };
		panel.Children.Add(Row(B("+ Keep this pose", KeepPose)));
		panel.Children.Add(PoseList);
		panel.Children.Add(Labelled("Name", PoseName));
		_needsPose.Add(PoseName);
		panel.Children.Add(Row(B("Put it on", () => ApplyPickedPose(false), _needsPose), B("Mirrored", () => ApplyPickedPose(true), _needsPose)));
		panel.Children.Add(Labelled("Amount", amount));
		panel.Children.Add(PoseOnlyPicked);
		panel.Children.Add(Row(B("Keep it again", UpdatePose, _needsPose), B("Delete", DeletePose, _needsPose)));
		panel.Children.Add(new TextBlock { Text = "The pose now", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
		panel.Children.Add(Row(B("Mirror", MirrorThePose), B("Copy", CopyThePose)));
		panel.Children.Add(Row(B("Paste", () => PasteThePose(false), _needsClipboard), B("Paste mirrored", () => PasteThePose(true), _needsClipboard)));
		panel.Children.Add(Row(_symmetrize));
		panel.Children.Add(_poseHint);

		PoseList.SelectionChanged += (_, _) =>
		{
			if (_syncingPoses || PoseList.SelectedItem is not ListBoxItem { Tag: SavedPose pose }) return;
			PickedPose = pose;
			RefreshPoses();
		};
		PoseList.DoubleTapped += (_, _) => ApplyPickedPose(false);
		PoseName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitPoseName(); Viewport.Focus(); } };
		PoseName.LostFocus += (_, _) => CommitPoseName();
		return new ScrollViewer { Content = panel };
	}

	private void Said(PoseChange change)
	{
		if (change == PoseChange.Unchanged && State.AnimateMode) _poseHint.Text = "That's how it's posed already.";
	}

	internal void ApplyPickedPose(bool mirrored)
	{
		if (PickedPose is not { } pose || SceneLevel) return;
		IReadOnlyCollection<object>? only = !mirrored && PoseOnlyPicked.IsChecked == true && State.Picked.Count > 0 ? State.Picked.ToHashSet() : null;
		Said(_session.ApplyPose(pose, mirrored, (float)(PoseAmount.Value / 100), only));
	}

	internal void KeepPose()
	{
		if (SceneLevel || _session.KeepPose() is not { } pose) return;
		PickedPose = pose;
		RefreshPoses();
		PoseName.Focus();
		PoseName.SelectAll();
	}

	private void UpdatePose()
	{
		if (PickedPose is not { } old) return;
		PickedPose = _session.UpdatePose(old);
		RefreshPoses();
	}

	private void DeletePose()
	{
		if (PickedPose is not { } pose) return;
		_session.DeletePose(pose);
		PickedPose = null;
		RefreshPoses();
	}

	internal void CommitPoseName()
	{
		if (_syncingPoses || PickedPose is not { } pose) return;
		string name = PoseName.Text?.Trim() ?? "";
		if (name.Length == 0 || name == pose.Name) return;
		if (!_session.RenamePose(pose, name))
		{
			_poseHint.Text = $"Another pose is called \"{name}\".";
			PoseName.Text = pose.Name;
			return;
		}
		RefreshPoses();
	}

	// Left and right swapped (the picked, or everything)
	internal void MirrorThePose()
	{
		if (!SceneLevel) Said(_session.MirrorPose());
	}

	internal void CopyThePose()
	{
		if (SceneLevel || _session.CopyPose() is not { } copied) return;
		_poseHint.Text = $"Copied the pose of {copied.Bones.Count} bone(s) and {copied.Parts.Count} part(s): paste it at another frame, or in another animation.";
		RefreshPoses();
	}

	internal void PasteThePose(bool mirrored)
	{
		if (SceneLevel) return;
		if (_session.PoseClipboard == null)
		{
			_poseHint.Text = "Copy a pose first (Ctrl+Shift+C).";
			return;
		}
		Said(_session.PastePose(mirrored));
	}

	// Build: the other side made the mirror image of the picked bones (or all of them)
	private void Symmetrize()
	{
		if (SceneLevel) return;
		if (State.AnimateMode)
		{
			_poseHint.Text = "Symmetrizing changes the rig itself: in Build (K).";
			return;
		}
		if (_session.Symmetrize(_session.PickedBonesOrAll() ?? State.Skeleton.AllBones().ToList()) is not { } made)
		{
			_poseHint.Text = "None of those bones' names says a side (like arm_L, Left Arm or hand.r), so there's no other side to make.";
			return;
		}
		_poseHint.Text = made > 0 ? $"Made {made} bone(s) on the other side." : "The other side now mirrors them.";
	}

	// The list made again (an edit, an undo, another object)
	internal void RefreshPoses()
	{
		var rig = State.Skeleton;
		if (PickedPose != null && !rig.Poses.Contains(PickedPose)) PickedPose = null;
		_syncingPoses = true;
		try
		{
			var items = new List<ListBoxItem>();
			ListBoxItem? picked = null;
			foreach (var pose in rig.Poses)
			{
				var item = new ListBoxItem
				{
					Content = $"{pose.Name}   ({pose.Bones.Count} bone{(pose.Bones.Count == 1 ? "" : "s")}{(pose.Parts.Count > 0 ? $", {pose.Parts.Count} part(s)" : "")})",
					Tag = pose
				};
				items.Add(item);
				if (pose == PickedPose) picked = item;
			}
			PoseList.ItemsSource = items;
			PoseList.SelectedItem = picked;
			foreach (var c in _needsPose) c.IsEnabled = PickedPose != null;
			foreach (var c in _needsClipboard) c.IsEnabled = _session.PoseClipboard != null;
			_symmetrize.IsEnabled = !State.AnimateMode;
			if (!PoseName.IsFocused) PoseName.Text = PickedPose?.Name ?? "";
			if (rig.Poses.Count == 0 && _poseHint.Text is null or "")
				_poseHint.Text = "Pose the rig, pick what the pose is of (or nothing, for all of it), then + Keep this pose. Mirroring goes by names that say a side: arm_L / arm_R, Left Arm / Right Arm.";
		}
		finally
		{
			_syncingPoses = false;
		}
	}
}
