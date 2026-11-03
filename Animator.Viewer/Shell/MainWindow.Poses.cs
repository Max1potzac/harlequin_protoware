using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Animator.Viewer;

// Poses and mirroring (the rig panel's Poses view; Ctrl+M, Ctrl+Shift+C / V, Ctrl+Alt+V):
// poses kept by name and put on again (partly, on just the picked, or mirrored onto the other
// side), the pose mirrored left to right, copied and pasted (mirrored too), and the rig
// symmetrized. Sides come from names (arm_L / arm_R, Left Arm / Right Arm…: see MirrorNames).
// In Animate mode they key the pose at the playhead (with auto key off, held until S); in Setup
// mode they change the rest pose.
public partial class MainWindow
{
	private bool _posesView;
	private SavedPose? _pose;            // the one selected in the list
	private bool _syncingPoses;

	private float PoseAmountNow => (float)(PoseAmount.Value / 100);

	// ======================= putting poses on =======================

	// (see EditorSession.PoseEdit)
	private void PoseEdit(string name, Action change)
	{
		if (_session.PoseEdit(name, change) == PoseChange.Unchanged && _state.AnimateMode) PoseHint.Text = "That's how it's posed already.";
	}

	private IReadOnlyCollection<object>? OnlyThePicked() =>
		PoseOnlyPicked.IsChecked == true && _state.Picked.Count > 0 ? _state.Picked.ToHashSet() : null;

	private void ApplyPose(SavedPose pose, bool mirrored)
	{
		if (_session.ApplyPose(pose, mirrored, PoseAmountNow, mirrored ? null : OnlyThePicked()) == PoseChange.Unchanged && _state.AnimateMode)
			PoseHint.Text = "That's how it's posed already.";
	}

	private void PoseApply_Click(object sender, RoutedEventArgs e)
	{
		if (_pose != null) ApplyPose(_pose, mirrored: false);
	}

	private void PoseApplyMirrored_Click(object sender, RoutedEventArgs e)
	{
		if (_pose != null) ApplyPose(_pose, mirrored: true);
	}

	private void PoseList_DoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (_pose != null) ApplyPose(_pose, mirrored: false);
	}

	private void PoseAmount_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (PoseAmountText != null) PoseAmountText.Text = $"{e.NewValue:0}%";
	}

	// ======================= mirroring, copy and paste =======================

	private List<Bone>? PickedBonesOrNull() => _session.PickedBonesOrAll();

	// Ctrl+M: left and right swapped (the picked, or everything)
	private void MirrorThePose()
	{
		if (_session.MirrorPose() == PoseChange.Unchanged && _state.AnimateMode) PoseHint.Text = "That's how it's posed already.";
	}

	private void MirrorPose_Click(object sender, RoutedEventArgs e) => MirrorThePose();

	private void CopyThePose()
	{
		if (_session.CopyPose() is not { } copied) return;
		PoseHint.Text = $"Copied the pose of {copied.Bones.Count} bone(s) and {copied.Parts.Count} part(s): paste it at another frame, or in another animation.";
		AnimHint.Text = "";
	}

	private void PasteThePose(bool mirrored)
	{
		if (_session.PoseClipboard == null)
		{
			PoseHint.Text = $"Copy a pose first ({KeyOf("pose.copy")}).";
			return;
		}
		if (_session.PastePose(mirrored) == PoseChange.Unchanged && _state.AnimateMode) PoseHint.Text = "That's how it's posed already.";
	}

	private void CopyPose_Click(object sender, RoutedEventArgs e) => CopyThePose();
	private void PastePose_Click(object sender, RoutedEventArgs e) => PasteThePose(mirrored: false);
	private void PasteMirrored_Click(object sender, RoutedEventArgs e) => PasteThePose(mirrored: true);

	// Setup mode: the other side made the mirror image of the picked bones (or all of them)
	private void Symmetrize_Click(object sender, RoutedEventArgs e)
	{
		if (_sceneLevel) return;
		if (_state.AnimateMode)
		{
			PoseHint.Text = $"Symmetrizing changes the rig itself: in Build ({KeyOf("anim.mode")}).";
			return;
		}
		if (_session.Symmetrize(PickedBonesOrNull() ?? _skeleton.AllBones().ToList()) is not { } made)
		{
			PoseHint.Text = "None of those bones' names says a side (like arm_L, Left Arm or hand.r), so there's no other side to make.";
			return;
		}
		PoseHint.Text = made > 0 ? $"Made {made} bone(s) on the other side (give them parts: + Part, or drag parts onto them)." : "The other side now mirrors them.";
	}

	// ======================= the list =======================

	private void PoseSave_Click(object sender, RoutedEventArgs e)
	{
		if (_session.KeepPose() is not { } pose) return;
		_pose = pose;
		BuildPosesPanel();
		PoseName.Focus();
		PoseName.SelectAll();
	}

	// The selected pose kept again, as its bones and parts are posed now
	private void PoseUpdate_Click(object sender, RoutedEventArgs e)
	{
		if (_pose is not { } old) return;
		_pose = _session.UpdatePose(old);
		BuildPosesPanel();
	}

	private void PoseDelete_Click(object sender, RoutedEventArgs e)
	{
		if (_pose is not { } pose) return;
		_session.DeletePose(pose);
		_pose = null;
		BuildPosesPanel();
	}

	private void PoseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingPoses || PoseList.SelectedItem is not ListBoxItem { Tag: SavedPose pose }) return;
		_pose = pose;
		BuildPosesPanel();
	}

	private void PoseName_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitPoseName();
		Viewport.Focus();
	}

	private void PoseName_LostFocus(object sender, RoutedEventArgs e) => CommitPoseName();

	private void CommitPoseName()
	{
		if (_syncingPoses || _pose is not { } pose) return;
		string name = PoseName.Text.Trim();
		if (name.Length == 0 || name == pose.Name) return;
		if (!_session.RenamePose(pose, name))
		{
			PoseHint.Text = $"Another pose is called \"{name}\".";
			PoseName.Text = pose.Name;
			return;
		}
		BuildPosesPanel();
	}

	private void BuildPosesPanel()
	{
		if (!_posesView) return;
		if (_pose != null && !_skeleton.Poses.Contains(_pose)) _pose = null;
		_syncingPoses = true;
		PoseList.Items.Clear();
		foreach (var pose in _skeleton.Poses)
		{
			var item = new ListBoxItem
			{
				Content = $"{pose.Name}   ({pose.Bones.Count} bone{(pose.Bones.Count == 1 ? "" : "s")}{(pose.Parts.Count > 0 ? $", {pose.Parts.Count} part(s)" : "")})",
				Tag = pose,
				Foreground = AdjustEditor.LabelBrush
			};
			PoseList.Items.Add(item);
			if (pose == _pose) PoseList.SelectedItem = item;
		}
		bool any = _pose != null;
		PoseUpdateButton.IsEnabled = PoseDeleteButton.IsEnabled = PoseApplyButton.IsEnabled = PoseApplyMirroredButton.IsEnabled = PoseName.IsEnabled = any;
		if (!PoseName.IsKeyboardFocused) PoseName.Text = _pose?.Name ?? "";
		PastePoseButton.IsEnabled = PasteMirroredButton.IsEnabled = _session.PoseClipboard != null;
		SymmetrizeButton.IsEnabled = !_state.AnimateMode;
		_syncingPoses = false;
		if (PoseHint.Text.Length == 0 || !any)
			PoseHint.Text = _skeleton.Poses.Count == 0
				? "Pose the rig, pick what the pose is of (or nothing, for all of it), then + Keep this pose. Mirroring goes by names that say a side: arm_L / arm_R, Left Arm / Right Arm, hand.l / hand.r."
				: _state.AnimateMode ? $"Animate: putting a pose on keys it at frame {_state.Frame}." : "Setup: putting a pose on makes it the rest pose.";
	}
}
