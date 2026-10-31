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
	private SavedPose? _poseClipboard;   // Copy pose
	private bool _syncingPoses;

	private float PoseAmountNow => (float)(PoseAmount.Value / 100);

	// ======================= putting poses on =======================

	// Changes the pose (`change` works on how the rig is posed now), then keys what changed here
	// (Animate mode; auto key off: holds it), or makes it the rest pose (Setup mode)
	private void PoseEdit(string name, Action change)
	{
		if (_sceneLevel) return;
		var animation = _state.Animation;
		if (_state.AnimateMode)
		{
			_state.Playing = false;
			_state.SetFrame(_state.Frame);
			var before = PoseValues.Capture(_skeleton);
			change();
			var after = PoseValues.Capture(_skeleton);
			if (!_state.AutoKey)
			{
				_state.Hold.Take(before, after, _skeleton, animation, _state.Frame);
				return;
			}
			var keys = AnimationSnapshot.Capture(animation);
			var changed = new PoseHold();
			if (!changed.Take(before, after, _skeleton, animation, _state.Frame))
			{
				PoseHint.Text = "That's how it's posed already.";
				return;
			}
			changed.KeyInto(animation, _state.Time);
			_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>($"{name} @ frame {_state.Frame}", keys,
				AnimationSnapshot.Capture(animation), s => s.Restore()));
			return;
		}

		// Setup: the rest pose (bones; parts keep their rest placement)
		_state.EditStructure(() =>
		{
			_skeleton.SetToSetupPose();
			_skeleton.UpdateWorld();
			change();
			bool any = false;
			foreach (var bone in _skeleton.AllBones())
			{
				if (bone.Position == bone.SetupPosition && bone.Rotation == bone.SetupRotation && bone.Scale == bone.SetupScale &&
					bone.Turn == bone.SetupTurn && bone.Tilt == bone.SetupTilt) continue;
				bone.SetupPosition = bone.Position;
				bone.SetupRotation = bone.Rotation;
				bone.SetupScale = bone.Scale;
				bone.SetupTurn = bone.Turn;
				bone.SetupTilt = bone.Tilt;
				any = true;
			}
			return any ? $"{name} (rest pose)" : null;
		});
	}

	private IReadOnlyCollection<object>? OnlyThePicked() =>
		PoseOnlyPicked.IsChecked == true && _state.Picked.Count > 0 ? _state.Picked.ToHashSet() : null;

	private void ApplyPose(SavedPose pose, bool mirrored)
	{
		float amount = PoseAmountNow;
		string how = amount < 0.999f ? $" ({amount:P0})" : "";
		if (mirrored) PoseEdit($"Put on {pose.Name} mirrored{how}", () => PoseOps.ApplyMirrored(_skeleton, pose, amount));
		else PoseEdit($"Put on {pose.Name}{how}", () => PoseOps.Apply(_skeleton, pose, amount, OnlyThePicked()));
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

	private List<Bone>? PickedBonesOrNull() => _state.Picked.Count > 0 ? _state.PickedBones.ToList() : null;
	private List<Slot>? PickedPartsOrNull() => _state.Picked.Count > 0 ? _state.PickedSlots.ToList() : null;

	// Ctrl+M: left and right swapped (the picked, or everything)
	private void MirrorThePose()
	{
		var bones = PickedBonesOrNull();
		var parts = PickedPartsOrNull();
		PoseEdit(bones == null ? "Mirror the pose" : $"Mirror {bones.Count + (parts?.Count ?? 0)} picked", () => PoseOps.Mirror(_skeleton, bones, parts));
	}

	private void MirrorPose_Click(object sender, RoutedEventArgs e) => MirrorThePose();

	private void CopyThePose()
	{
		if (_sceneLevel) return;
		_poseClipboard = PoseOps.Capture(_skeleton, "Copied pose", PickedBonesOrNull(), PickedPartsOrNull());
		PoseHint.Text = $"Copied the pose of {_poseClipboard.Bones.Count} bone(s) and {_poseClipboard.Parts.Count} part(s): paste it at another frame, or in another animation.";
		AnimHint.Text = "";
	}

	private void PasteThePose(bool mirrored)
	{
		if (_poseClipboard is not { } pose)
		{
			PoseHint.Text = $"Copy a pose first ({KeyOf("pose.copy")}).";
			return;
		}
		if (mirrored) PoseEdit("Paste the pose mirrored", () => PoseOps.ApplyMirrored(_skeleton, pose));
		else PoseEdit("Paste the pose", () => PoseOps.Apply(_skeleton, pose));
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
			PoseHint.Text = $"Symmetrizing changes the rig itself: in Setup mode ({KeyOf("anim.mode")}).";
			return;
		}
		var bones = PickedBonesOrNull() ?? _skeleton.AllBones().ToList();
		var sided = bones.Where(b => MirrorNames.Other(b.Name) != null).ToList();
		if (sided.Count == 0)
		{
			PoseHint.Text = "None of those bones' names says a side (like arm_L, Left Arm or hand.r), so there's no other side to make.";
			return;
		}
		int made = 0;
		_state.EditStructure(() =>
		{
			made = PoseOps.Symmetrize(_skeleton, sided).Count;
			return $"Symmetrize {sided.Count} bone(s)";
		});
		PoseHint.Text = made > 0 ? $"Made {made} bone(s) on the other side (give them parts: + Part, or drag parts onto them)." : "The other side now mirrors them.";
	}

	// ======================= the list =======================

	private string UniquePoseName(string baseName)
	{
		var used = _skeleton.Poses.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
			if (!used.Contains($"{baseName} {i}")) return $"{baseName} {i}";
	}

	private void EditPoses(string name, Action change)
	{
		var before = _skeleton.Poses.Select(p => (p, p.Name)).ToList();
		change();
		var after = _skeleton.Poses.Select(p => (p, p.Name)).ToList();
		_state.Undo.Push(new SnapshotCommand<List<(SavedPose, string)>>(name, before, after, list =>
		{
			_skeleton.Poses.Clear();
			foreach (var (pose, poseName) in list)
			{
				pose.Name = poseName;
				_skeleton.Poses.Add(pose);
			}
		}));
	}

	private void PoseSave_Click(object sender, RoutedEventArgs e)
	{
		if (_sceneLevel) return;
		var pose = PoseOps.Capture(_skeleton, UniquePoseName(_state.AnimateMode ? $"{_state.Animation.Name} {_state.Frame}" : "Rest"),
			PickedBonesOrNull(), PickedPartsOrNull());
		EditPoses($"Keep the pose {pose.Name}", () => _skeleton.Poses.Add(pose));
		_pose = pose;
		BuildPosesPanel();
		PoseName.Focus();
		PoseName.SelectAll();
	}

	// The selected pose kept again, as its bones and parts are posed now
	private void PoseUpdate_Click(object sender, RoutedEventArgs e)
	{
		if (_pose is not { } old) return;
		var bones = old.Bones.Keys.Select(_skeleton.FindBone).OfType<Bone>().ToList();
		var parts = old.Parts.Keys.Select(_skeleton.FindSlot).OfType<Slot>().ToList();
		var fresh = PoseOps.Capture(_skeleton, old.Name, bones, parts);
		int i = _skeleton.Poses.IndexOf(old);
		EditPoses($"Update the pose {old.Name}", () => _skeleton.Poses[i] = fresh);
		_pose = fresh;
		BuildPosesPanel();
	}

	private void PoseDelete_Click(object sender, RoutedEventArgs e)
	{
		if (_pose is not { } pose) return;
		EditPoses($"Delete the pose {pose.Name}", () => _skeleton.Poses.Remove(pose));
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
		if (_skeleton.Poses.Any(p => p != pose && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
		{
			PoseHint.Text = $"Another pose is called \"{name}\".";
			PoseName.Text = pose.Name;
			return;
		}
		EditPoses($"Rename the pose to {name}", () => pose.Name = name);
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
		PastePoseButton.IsEnabled = PasteMirroredButton.IsEnabled = _poseClipboard != null;
		SymmetrizeButton.IsEnabled = !_state.AnimateMode;
		_syncingPoses = false;
		if (PoseHint.Text.Length == 0 || !any)
			PoseHint.Text = _skeleton.Poses.Count == 0
				? "Pose the rig, pick what the pose is of (or nothing, for all of it), then + Keep this pose. Mirroring goes by names that say a side: arm_L / arm_R, Left Arm / Right Arm, hand.l / hand.r."
				: _state.AnimateMode ? $"Animate: putting a pose on keys it at frame {_state.Frame}." : "Setup: putting a pose on makes it the rest pose.";
	}
}
