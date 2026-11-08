using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// The mixer: tracks played over each other in the viewport, a fade tried out, fades kept (and
// undone) for a pair, the default fade, and the window closing with its object
public class MixerTests
{
	private static void Toggle(ToggleButton button)
	{
		button.IsChecked = !(button.IsChecked == true);
		button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
	}

	[AvaloniaFact]
	public void Animations_are_mixed_and_fades_kept()
	{
		var window = Windows.Show();
		var state = window.State;
		var root = state.Skeleton.Roots[0];
		var walk = state.Animation;
		walk.Name = "walk";
		var turn = new RotateTimeline("root");
		turn.Track.Add(0f, 0f);
		turn.Track.Add(1f, 1f);
		turn.Track.Add(2f, 0f);
		walk.Timelines.Add(turn);
		var wave = window.Session.NewAnimation("wave");
		window.SwitchAnimation(walk);
		window.Frame(0);
		float rest = root.Rotation;

		window.OpenMixer();
		var mixer = window.Mixer!;
		Assert.NotNull(mixer);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(3, mixer.TrackRows.Count);
		Assert.Equal("walk", mixer.TrackRows[0].Pick.SelectedItem!.ToString());

		// The tracks play in the viewport, on the mixer's clock
		Toggle(mixer.PreviewButton);
		Assert.True(window.Mixing);
		Assert.False(state.Playing);
		window.Frame(1f);
		Assert.True(MathF.Abs(root.Rotation - rest) > 0.5f);

		// A fade kept for walk → wave, undone in the main window
		var plan = state.Skeleton.Mixes;
		mixer.DurationBox.Text = "0.5";
		mixer.KeepPair();
		Assert.Equal(0.5f, plan.Duration("walk", "wave"), 3);
		Assert.Single(mixer.PairList.ItemsSource!.Cast<object>());
		Assert.True(mixer.ForgetButton.IsEnabled);
		state.Undo.Undo();
		Assert.Empty(plan.Pairs);
		Assert.Empty(mixer.PairList.ItemsSource!.Cast<object>());

		// The default fade
		mixer.DefaultBox.Text = "0.3";
		mixer.CommitDefault();
		Assert.Equal(0.3f, plan.Default, 3);

		// Tried out: the preview fades from walk into wave
		mixer.DurationBox.Text = "x";
		mixer.StartTrying();
		Assert.Contains("isn't a number", mixer.Hint.Text);
		mixer.DurationBox.Text = "0.25";
		mixer.StartTrying();
		Assert.True(mixer.TryButton.IsChecked);
		Assert.False(mixer.PreviewButton.IsChecked);
		Assert.True(window.Mixing);
		Assert.NotNull(window.Mixer);
		Windows.Shot(window, "mixer-main");
		var frame = mixer.CaptureRenderedFrame();
		Assert.NotNull(frame);
		frame!.Save(Path.Combine(Windows.ShotsFolder, "mixer.png"));

		// Back to the scene: the mixer closes, the timeline poses again
		window.LeaveObject();
		window.Frame(0);
		Dispatcher.UIThread.RunJobs();
		Assert.Null(window.Mixer);
		Assert.False(window.Mixing);
		Assert.NotSame(wave, walk);
	}
}
