using Animator.Core.Editing;
using Animator.Viewer.Files;
using Animator.Viewer.Tools;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// File > Settings: keys, mouse buttons, saving, the look; autosave and recovery
public class SettingsTests
{
	private static void Finish(Task task)
	{
		var until = DateTime.UtcNow.AddSeconds(30);
		while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		Assert.True(task.IsCompleted);
		task.GetAwaiter().GetResult();
	}

	// Back to the defaults, for the tests after this one
	private static void Restore(MainWindow window)
	{
		window.AnswerSettings = dialog =>
		{
			dialog.Tabs.SelectedIndex = 0;
			foreach (var id in KeyMap.Commands.Select(c => c.Id)) dialog.Keys.Reset(id);
			dialog.PickButtons(PointerButton.Left, PointerButton.Right, PointerButton.Middle);
			dialog.PickSaving(5, 3);
			dialog.PickTheme("Dark");
			dialog.PickSize(1.0);
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Finish(window.OpenSettings());
	}

	[AvaloniaFact]
	public void A_key_given_to_a_command_does_it()
	{
		var window = Windows.Show();
		window.AnswerSettings = dialog =>
		{
			dialog.Show(window);
			Dispatcher.UIThread.RunJobs();
			dialog.Pick("play.toggle");
			Assert.Contains("Space", dialog.CommandKeys.Text);

			// R is "back to the first frame": said, and Set takes it from there
			dialog.CaptureKey(Key.R, KeyModifiers.None);
			Assert.Contains("Back to the first frame", dialog.ConflictText.Text);
			dialog.CaptureKey(Key.F9, KeyModifiers.None);
			Assert.Equal("", dialog.ConflictText.Text);
			dialog.SetKeys(add: false);
			Assert.Contains("F9", dialog.CommandKeys.Text);
			Assert.Contains("at first: Space", dialog.CommandKeys.Text);
			var frame = dialog.CaptureRenderedFrame();
			Assert.NotNull(frame);
			Directory.CreateDirectory(Windows.ShotsFolder);
			frame!.Save(Path.Combine(Windows.ShotsFolder, "settings-keys.png"));

			// The filter finds a command by its key
			dialog.FilterBox.Text = "F9";
			Assert.Single(dialog.CommandList.Items);
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Finish(window.OpenSettings());

		window.State.Playing = false;
		Assert.False(window.HandleKey(Key.Space, KeyModifiers.None));   // (nothing does Space now)
		Assert.True(window.HandleKey(Key.F9, KeyModifiers.None));
		Assert.True(window.State.Playing);
		window.State.Playing = false;
		Assert.Equal(new[] { "F9" }, window.Settings.Shortcuts!["play.toggle"]);
		Assert.Equal(new[] { "F9" }, AppSettings.Load().Shortcuts!["play.toggle"]);   // remembered
		Restore(window);
		Assert.True(window.HandleKey(Key.Space, KeyModifiers.None));
		window.State.Playing = false;
	}

	[AvaloniaFact]
	public void The_mouse_buttons_must_differ_and_the_look_shows_at_once()
	{
		var window = Windows.Show();
		window.AnswerSettings = dialog =>
		{
			dialog.Show(window);
			dialog.PickButtons(PointerButton.Left, PointerButton.Left, PointerButton.Middle);
			dialog.TryOk();
			Assert.Contains("a mouse button of their own", dialog.MouseError.Text);
			Assert.True(dialog.IsVisible);

			dialog.PickButtons(PointerButton.Right, PointerButton.Left, PointerButton.Middle);
			dialog.PickSaving(1, 10);
			dialog.Tabs.SelectedIndex = 3;
			dialog.PickTheme("Light");
			dialog.PickSize(1.25);
			Assert.Equal(ThemeVariant.Light, Avalonia.Application.Current!.RequestedThemeVariant);
			Assert.Equal(1.25, UiSize.Current);
			Dispatcher.UIThread.RunJobs();
			var frame = dialog.CaptureRenderedFrame();
			frame!.Save(Path.Combine(Windows.ShotsFolder, "settings-appearance.png"));
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Finish(window.OpenSettings());
		Assert.Equal(PointerButton.Right, window.Settings.SelectButton);
		Assert.Equal(1, window.Settings.AutosaveMinutes);
		Assert.Equal(10, window.Settings.BackupCount);
		Assert.Equal("Light", window.Settings.Theme);
		Windows.Shot(window, "settings-light-125");

		// Cancel puts the look back
		window.AnswerSettings = dialog =>
		{
			dialog.Show(window);
			dialog.PickTheme("Dark");
			dialog.PickSize(1.0);
			dialog.Close(false);
			return Task.FromResult(false);
		};
		Finish(window.OpenSettings());
		Assert.Equal(ThemeVariant.Light, Avalonia.Application.Current!.RequestedThemeVariant);
		Assert.Equal(1.25, UiSize.Current);
		Restore(window);
		Assert.Equal(ThemeVariant.Dark, Avalonia.Application.Current!.RequestedThemeVariant);
		Assert.Equal(1.0, UiSize.Current);
	}

	[AvaloniaFact]
	public void Unsaved_work_is_kept_aside_and_offered_back()
	{
		var window = Windows.Show();
		window.Settings.AutosaveMinutes = 1;
		var now = DateTime.UtcNow;
		window.Autosave.Now = () => now;
		Assert.False(window.AutosaveTick());   // (the clock starts)

		var bone = window.State.Skeleton.Roots[0];
		float was = bone.SetupRotation;
		bone.SetupRotation = was + 0.5f;
		window.State.Undo.Push(new SnapshotCommand<float>("Turn", was, was + 0.5f, v => bone.SetupRotation = v));
		Assert.False(window.AutosaveTick());   // not a minute yet
		now = now.AddMinutes(2);
		Assert.True(window.AutosaveTick());
		var until = DateTime.UtcNow.AddSeconds(30);
		while (window.Autosave.Keeping && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		string copy = window.Autosave.CopyFile!;
		Assert.True(File.Exists(copy));

		// As if this run had crashed: the next one is offered the copy, and opens it unsaved
		var note = System.Text.Json.JsonSerializer.Deserialize<Recovery.Note>(File.ReadAllText(copy + ".json"))!;
		note.ProcessId = int.MaxValue;
		Recovery.WriteNote(copy, note);
		var next = Windows.Show();
		Recovery.Leftover? offered = null;
		next.AnswerRecovery = left =>
		{
			offered = left;
			return Task.FromResult<string?>("Open");
		};
		Finish(next.OfferRecovery());
		Assert.NotNull(offered);
		Assert.Equal(copy, offered!.File);
		Assert.Equal(was + 0.5f, next.State.Skeleton.Roots[0].SetupRotation, 4);
		Assert.EndsWith("* — Harlequin", next.Title);

		// Thrown away: gone, and not offered again
		Recovery.Remove(copy);
		window.State.Undo.Undo();
	}
}
