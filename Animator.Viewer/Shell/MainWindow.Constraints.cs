using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Animator.Viewer;

// The bone panel's "Follow and jiggle" box: a copy constraint (the bone follows another one's
// turn, place or size, in the object or locally) and a jiggle constraint (it lags behind and
// springs back as time goes by). Undoable; a slider drag is one step.
public partial class MainWindow
{
	private bool _syncingConstraints;
	private StructureSnapshot? _constraintDrag;   // before a slider drag

	private const string NoBone = "(none)";

	private CopyConstraint? CopyOf(Bone? bone) => _session.CopyOf(bone);
	private JiggleConstraint? JiggleOf(Bone? bone) => _session.JiggleOf(bone);

	private void SyncConstraintPanel()
	{
		var bone = _state.SelectedSlot == null ? _state.Selected : null;
		bool shown = bone != null && !_sceneLevel && !IsDrawn && BoneProps.Visibility == Visibility.Visible;
		ConstraintBox.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
		if (!shown || bone == null) return;

		_syncingConstraints = true;
		var copy = CopyOf(bone);
		var jiggle = JiggleOf(bone);
		var names = new[] { NoBone }.Concat(_skeleton.AllBones().Where(b => b != bone).Select(b => b.Name)).ToList();
		if (!CopySourceBox.Items.Cast<string>().SequenceEqual(names))
		{
			CopySourceBox.Items.Clear();
			foreach (var name in names) CopySourceBox.Items.Add(name);
		}
		CopySourceBox.SelectedItem = copy?.Source.Name ?? NoBone;
		CopyLocalBox.IsChecked = copy?.Local == true;
		CopyLocalBox.IsEnabled = CopyRotate.IsEnabled = CopyMove.IsEnabled = CopyScale.IsEnabled = copy != null;
		CopyKeepButton.IsEnabled = copy != null && !copy.Local;
		SetSlider(CopyRotate, copy?.RotateMix ?? 1f, "P0");
		SetSlider(CopyMove, copy?.MoveMix ?? 0f, "P0");
		SetSlider(CopyScale, copy?.ScaleMix ?? 0f, "P0");
		JiggleBox.IsChecked = jiggle != null;
		JiggleStiffness.IsEnabled = JiggleDamping.IsEnabled = JiggleGravity.IsEnabled = jiggle != null;
		SetSlider(JiggleStiffness, jiggle?.Stiffness ?? 80f, "0");
		SetSlider(JiggleDamping, jiggle?.Damping ?? 6f, "0.#");
		SetSlider(JiggleGravity, jiggle?.Gravity ?? 0f, "0");
		ConstraintMix.IsEnabled = copy != null || jiggle != null;
		SetSlider(ConstraintMix, copy?.SetupMix ?? jiggle?.SetupMix ?? 1f, "P0");
		_syncingConstraints = false;

		ConstraintHint.Text = (copy, jiggle) switch
		{
			(null, null) => "Pick a bone for it to follow, or let it jiggle.",
			_ => string.Join(" ", new[]
			{
				copy == null ? null : copy.Local ? $"It moves as {copy.Source.Name} moves." : $"It follows {copy.Source.Name} in the object.",
				jiggle == null ? null : $"It jiggles in Animate (play to see it swing{(_state.AnimateMode ? "" : ": switch to Animate")})."
			}.Where(s => s != null))
		};
	}

	private void SetSlider(Slider slider, float value, string format)
	{
		if (!slider.IsMouseCaptureWithin) slider.Value = value;
		if (FindName(slider.Name + "Text") is TextBlock text) text.Text = value.ToString(format);
	}

	private void CopySource_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingConstraints || _state.Selected is not { } bone || CopySourceBox.SelectedItem is not string name) return;
		if (_session.SetCopySource(bone, name == NoBone ? null : _skeleton.FindBone(name))) SyncConstraintPanel();
	}

	private void CopyLocal_Click(object sender, RoutedEventArgs e)
	{
		if (CopyOf(_state.Selected) is not { } copy) return;
		_session.SetCopyLocal(copy, CopyLocalBox.IsChecked == true);
		SyncConstraintPanel();
	}

	private void CopyKeep_Click(object sender, RoutedEventArgs e)
	{
		if (CopyOf(_state.Selected) is not { } copy) return;
		_session.KeepCopyPlace(copy);
		SyncConstraintPanel();
	}

	private void Jiggle_Click(object sender, RoutedEventArgs e)
	{
		if (_state.Selected is not { } bone) return;
		_session.SetJiggle(bone, JiggleBox.IsChecked == true);
		SyncConstraintPanel();
	}

	// ----- sliders: a click is one step; a drag, one step when let go -----

	private void ConstraintSlider_DragStarted(object sender, DragStartedEventArgs e) =>
		_constraintDrag ??= _state.CaptureStructure();

	private void ConstraintSlider_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		if (_constraintDrag is not { } before || sender is not Slider slider) return;
		_constraintDrag = null;
		_state.PushStructure(before, $"{_state.Selected?.Name}: {EditorSession.NameOf(Which(slider))}");
		SyncConstraintPanel();
	}

	private static ConstraintValue Which(Slider slider) =>
		Enum.TryParse<ConstraintValue>((string)slider.Tag, out var which) ? which : ConstraintValue.Mix;

	private void ConstraintSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncingConstraints || !IsLoaded || sender is not Slider slider || _state.Selected is not { } bone) return;
		float v = (float)e.NewValue;
		var which = Which(slider);
		if (_constraintDrag == null)
			_session.StepConstraintValue(bone, which, v);   // (a click: a step of its own)
		else if (_session.SetConstraintValue(bone, which, v) && FindName(slider.Name + "Text") is TextBlock text)
			text.Text = v.ToString(slider.Maximum > 1 ? "0" : "P0");
	}
}
