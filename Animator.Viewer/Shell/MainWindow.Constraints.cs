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

	private CopyConstraint? CopyOf(Bone? bone) => bone == null ? null : _skeleton.CopyConstraints.Find(c => c.Bone == bone);
	private JiggleConstraint? JiggleOf(Bone? bone) => bone == null ? null : _skeleton.JiggleConstraints.Find(c => c.Bone == bone);

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

	private void EditConstraints(string name, Func<bool> change)
	{
		if (_sceneLevel) return;
		_state.EditStructure(() => change() ? name : null);
		_skeleton.UpdatePose();
		SyncConstraintPanel();
	}

	private void CopySource_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingConstraints || _state.Selected is not { } bone || CopySourceBox.SelectedItem is not string name) return;
		var copy = CopyOf(bone);
		var source = name == NoBone ? null : _skeleton.FindBone(name);
		if (source == copy?.Source) return;
		if (source == null)
		{
			EditConstraints($"{bone.Name}: follow nothing", () => copy != null && _skeleton.CopyConstraints.Remove(copy));
			return;
		}
		EditConstraints($"{bone.Name}: follow {source.Name}", () =>
		{
			if (copy == null)
			{
				copy = new CopyConstraint { Name = $"{bone.Name}_follow", Bone = bone };
				_skeleton.CopyConstraints.Add(copy);
			}
			copy.Source = source;
			_skeleton.SetToSetupPose();   // (offsets: where it is at rest)
			_skeleton.UpdateWorld();
			copy.KeepOffsets();
			return true;
		});
	}

	private void CopyLocal_Click(object sender, RoutedEventArgs e)
	{
		if (CopyOf(_state.Selected) is not { } copy) return;
		bool local = CopyLocalBox.IsChecked == true;
		EditConstraints(local ? $"{copy.Bone.Name}: follow how {copy.Source.Name} moves" : $"{copy.Bone.Name}: follow where {copy.Source.Name} is",
			() => { copy.Local = local; return true; });
	}

	private void CopyKeep_Click(object sender, RoutedEventArgs e)
	{
		if (CopyOf(_state.Selected) is not { } copy) return;
		EditConstraints($"{copy.Bone.Name}: keep its place from {copy.Source.Name}", () =>
		{
			float mix = copy.Mix;
			_skeleton.UpdatePose();
			copy.Mix = 0f;   // (where it is without the constraint)
			_skeleton.SetToSetupPose();
			_skeleton.UpdateWorld();
			copy.KeepOffsets();
			copy.Mix = mix;
			return true;
		});
	}

	private void Jiggle_Click(object sender, RoutedEventArgs e)
	{
		if (_state.Selected is not { } bone) return;
		var jiggle = JiggleOf(bone);
		if (JiggleBox.IsChecked == true && jiggle == null)
			EditConstraints($"{bone.Name}: jiggle", () =>
			{
				_skeleton.JiggleConstraints.Add(new JiggleConstraint { Name = $"{bone.Name}_jiggle", Bone = bone });
				return true;
			});
		else if (JiggleBox.IsChecked != true && jiggle != null)
			EditConstraints($"{bone.Name}: no jiggle", () => _skeleton.JiggleConstraints.Remove(jiggle));
	}

	// ----- sliders: a click is one step; a drag, one step when let go -----

	private void ConstraintSlider_DragStarted(object sender, DragStartedEventArgs e) =>
		_constraintDrag ??= _state.CaptureStructure();

	private void ConstraintSlider_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		if (_constraintDrag is not { } before || sender is not Slider slider) return;
		_constraintDrag = null;
		_state.PushStructure(before, $"{_state.Selected?.Name}: {SliderName(slider)}");
		SyncConstraintPanel();
	}

	private static string SliderName(Slider slider) => (string)slider.Tag switch
	{
		"CopyRotate" => "follow its turn",
		"CopyMove" => "follow its place",
		"CopyScale" => "follow its size",
		"JiggleStiffness" => "jiggle stiffness",
		"JiggleDamping" => "jiggle damping",
		"JiggleGravity" => "jiggle gravity",
		_ => "constraint mix"
	};

	private void ConstraintSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncingConstraints || !IsLoaded || sender is not Slider slider || _state.Selected is not { } bone) return;
		var copy = CopyOf(bone);
		var jiggle = JiggleOf(bone);
		float v = (float)e.NewValue;
		var before = _constraintDrag == null ? _state.CaptureStructure() : null;   // (a click: a step of its own)
		switch ((string)slider.Tag)
		{
			case "CopyRotate" when copy != null: copy.RotateMix = v; break;
			case "CopyMove" when copy != null: copy.MoveMix = v; break;
			case "CopyScale" when copy != null: copy.ScaleMix = v; break;
			case "JiggleStiffness" when jiggle != null: jiggle.Stiffness = v; break;
			case "JiggleDamping" when jiggle != null: jiggle.Damping = v; break;
			case "JiggleGravity" when jiggle != null: jiggle.Gravity = v; break;
			case "ConstraintMix":
				if (copy != null) copy.SetupMix = copy.Mix = v;
				if (jiggle != null) jiggle.SetupMix = jiggle.Mix = v;
				if (copy == null && jiggle == null) return;
				break;
			default: return;
		}
		if (before != null) _state.PushStructure(before, $"{bone.Name}: {SliderName(slider)}");
		else if (FindName(slider.Name + "Text") is TextBlock text) text.Text = v.ToString(slider.Maximum > 1 ? "0" : "P0");
	}
}
