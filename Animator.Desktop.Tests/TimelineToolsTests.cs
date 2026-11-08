using Animator.Core.Keyframing;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// The timeline's tools: events, auto key, keys moved about in time, frames in and out, playback
public class TimelineToolsTests
{
	private static void Finish(Task task)
	{
		var until = DateTime.UtcNow.AddSeconds(30);
		while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		Assert.True(task.IsCompleted);
		task.GetAwaiter().GetResult();
	}

	private static void Click(ContextMenu menu, string startsWith) =>
		menu.Items.OfType<MenuItem>().First(i => (i.Header as string)!.StartsWith(startsWith)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

	private static List<int> SelectedFrames(MainWindow window) => window.State.SelectedKeys.Select(k => k.Frame).Order().ToList();

	[AvaloniaFact]
	public void Events_are_added_edited_and_flash_while_playing()
	{
		var window = Windows.Show();
		window.AnswerEvent = dialog =>
		{
			dialog.NameBox.Text = "step";
			dialog.DataBox.Text = "left";
			dialog.VolumeBox.Text = "50";
			dialog.PitchBox.Text = "9";
			dialog.TryOk();
			Assert.Contains("Pitch", dialog.ErrorText.Text);
			dialog.PitchBox.Text = "1.5";
			dialog.TryOk();
			return Task.FromResult(dialog.ErrorText.Text == "");
		};
		Finish(window.AddEventAt(6));
		var events = window.State.Animation.Events!;
		var step = Assert.Single(events.Track.Keys).Value;
		Assert.Equal(("step", "left", 0.5f, 1.5f), (step.Name, step.Data, step.Volume, step.Pitch));

		// E: one at the playhead
		window.State.SetFrame(12);
		window.AnswerEvent = dialog =>
		{
			dialog.NameBox.Text = "hit";
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Assert.True(window.HandleKey(Key.E, KeyModifiers.None));
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(2, events.Track.Keys.Count);

		// Edited: renamed
		window.AnswerEvent = dialog =>
		{
			Assert.Equal("step", dialog.NameBox.Text);
			dialog.NameBox.Text = "stomp";
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Finish(window.EditEventAt(events, 6));
		Assert.Contains(events.Track.Keys, k => k.Value.Name == "stomp");

		// Played past: flashed over the viewport (in Animate: nothing plays while building)
		window.State.AnimateMode = true;
		window.State.SetFrame(0);
		window.State.Playing = true;
		for (int i = 0; i < 5; i++) window.Frame(0.05f);
		window.State.Playing = false;
		Assert.Contains("stomp (left)", window.EventFlash);
		Windows.Shot(window, "timeline-events");
	}

	[AvaloniaFact]
	public void Keys_move_about_in_time_and_frames_go_in_and_out()
	{
		var window = Windows.Show();
		var state = window.State;
		var root = state.Skeleton.Roots[0];
		state.AnimateMode = true;
		state.Pick(new object[] { root });
		state.SetFrame(0);
		window.KeyThePose();
		state.SetFrame(10);
		root.Rotation += 0.5f;
		window.KeyThePose();
		state.SetFrame(0);

		// Every key from frame 0 on (the empty spot's menu)
		Click(window.FrameMenu(0, null), "Select every key from frame 0 on");
		var keyed = SelectedFrames(window);
		Assert.Equal(new[] { 0, 10 }, keyed.Distinct());

		// Alt+Right: a frame later; the key menu: reversed
		Assert.True(window.HandleKey(Key.Right, KeyModifiers.Alt));
		Assert.Equal(new[] { 1, 11 }, SelectedFrames(window).Distinct());
		var menu = window.KeyMenu(null);
		Assert.Contains(menu.Items.OfType<MenuItem>(), i => i.Header as string == "Smooth");
		Click(menu, "Reverse");
		Assert.Equal(new[] { 1, 11 }, SelectedFrames(window).Distinct());
		state.Undo.Undo();
		state.Undo.Undo();
		Click(window.FrameMenu(0, null), "Select every key from frame 0 on");   // (an undo leaves the selection as it was)
		Assert.Equal(new[] { 0, 10 }, SelectedFrames(window).Distinct());

		// Alt+=: a frame inserted after the playhead (the keys after it move on), Alt+-: taken out
		int frames = state.FrameCount;
		state.SetFrame(4);
		Assert.True(window.HandleKey(Key.OemPlus, KeyModifiers.Alt));
		Assert.Equal(frames + 1, state.FrameCount);
		Click(window.FrameMenu(0, null), "Select every key from frame 0 on");
		Assert.Equal(new[] { 0, 11 }, SelectedFrames(window).Distinct());
		Assert.True(window.HandleKey(Key.OemMinus, KeyModifiers.Alt));
		Assert.Equal(frames, state.FrameCount);
	}

	[AvaloniaFact]
	public void Auto_key_rows_and_playback_are_set_and_remembered()
	{
		var window = Windows.Show();
		var state = window.State;
		window.AutoKeyBox.IsChecked = false;
		Assert.False(state.AutoKey);
		Assert.False(window.Settings.AutoKey);
		Assert.True(window.HandleKey(Key.K, KeyModifiers.Shift));
		Assert.True(state.AutoKey);
		Assert.True(window.AutoKeyBox.IsChecked);

		window.OnlyPickedBox.IsChecked = true;
		Assert.True(window.Settings.OnlyPickedRows);
		window.OnlyPickedBox.IsChecked = false;

		window.SpeedBox.SelectedIndex = 2;
		Assert.Equal(0.5f, state.Speed);
		Assert.Equal(0.5f, window.Settings.PlaybackSpeed);
		window.SpeedBox.SelectedIndex = 3;

		// Stopping goes back to where it started, when that's asked for
		window.Settings.StopGoesBack = true;
		state.SetFrame(5);
		window.TogglePlaying();
		for (int i = 0; i < 4; i++) window.Frame(0.1f);
		Assert.NotEqual(5, state.Frame);
		window.TogglePlaying();
		Assert.Equal(5, state.Frame);
		window.Settings.StopGoesBack = false;
		window.Settings.Save();
		Windows.Shot(window, "timeline-tools");
	}
}
