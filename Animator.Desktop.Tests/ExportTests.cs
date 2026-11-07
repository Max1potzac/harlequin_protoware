using Animator.Viewer.Export;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// File > Export: the dialog, and what it writes
public class ExportTests
{
	private static ExportWindow.FormatChoice FormatOf(ExportFormat format) => ExportWindow.Formats.First(f => f.Format == format);

	private static MainWindow WithLayersProject()
	{
		var window = Windows.Show();
		Assert.True(window.TryOpen(Windows.LayersProject(), out string? problem), problem);
		Windows.Settle(window);
		return window;
	}

	// Runs an export to its end (it gives the window a turn between steps)
	private static void Finish(Task task)
	{
		var until = DateTime.UtcNow.AddSeconds(60);
		while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		Assert.True(task.IsCompleted, "the export finished");
		task.GetAwaiter().GetResult();   // (what went wrong, if anything did)
	}

	[AvaloniaFact]
	public void The_dialog_shows_the_options_of_the_format_picked()
	{
		var window = WithLayersProject();
		var obj = window.Session.Scene.Objects[0];
		var dialog = new ExportWindow(window.Session.Scene, obj, 30, null, null, false);
		dialog.Show(window);
		Dispatcher.UIThread.RunJobs();
		Assert.Contains("animation", dialog.AnimationTicks[0].Content as string);

		// A game engine takes an object, not the scene
		dialog.SceneChoice.IsChecked = true;
		dialog.FormatBox.SelectedItem = FormatOf(ExportFormat.Engine);
		Assert.True(dialog.ObjectChoiceButton.IsChecked);
		Assert.Contains("hqrig.json", dialog.FormatHint.Text);
		Assert.True(dialog.AtlasSizeBox.IsEffectivelyVisible);
		Assert.False(dialog.LoopBox.IsEffectivelyVisible);
		var frame = dialog.CaptureRenderedFrame();
		Assert.NotNull(frame);
		Directory.CreateDirectory(Windows.ShotsFolder);
		frame!.Save(Path.Combine(Windows.ShotsFolder, "export-engine.png"));

		// Wrong numbers are said, and it stays open
		dialog.FormatBox.SelectedItem = FormatOf(ExportFormat.Gif);
		Assert.True(dialog.LoopBox.IsEffectivelyVisible);
		dialog.StepBox.Text = "0";
		dialog.TryExport();
		Assert.Contains("whole number from 1 to 60", dialog.ErrorText.Text);
		Assert.True(dialog.IsVisible);
		dialog.StepBox.Text = "1";
		foreach (var tick in dialog.AnimationTicks) tick.IsChecked = false;
		dialog.TryExport();
		Assert.Contains("Tick at least one animation", dialog.ErrorText.Text);

		// The scene, as a video: always a background
		dialog.SceneChoice.IsChecked = true;
		dialog.FormatBox.SelectedItem = FormatOf(ExportFormat.Video);
		Assert.False(dialog.TransparentBox.IsChecked);
		Assert.True(dialog.FfmpegBox.IsEffectivelyVisible);
		frame = dialog.CaptureRenderedFrame();
		frame!.Save(Path.Combine(Windows.ShotsFolder, "export-video.png"));

		// The choices made
		dialog.FormatBox.SelectedItem = FormatOf(ExportFormat.SpriteSheet);
		dialog.ScaleBox.Text = "0.5";
		dialog.TryExport();
		Assert.False(dialog.IsVisible);
		Assert.True(dialog.Settings.Scene);
		Assert.Equal(ExportFormat.SpriteSheet, dialog.Settings.Format);
		Assert.Equal(0.5f, dialog.Settings.Scale);
	}

	[AvaloniaFact]
	public void An_object_goes_out_for_a_game_engine()
	{
		var window = WithLayersProject();
		string folder = Path.Combine(Path.GetTempPath(), "hq-export-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		string? told = null;
		window.AnswerExport = dialog =>
		{
			dialog.ObjectChoiceButton.IsChecked = true;
			dialog.FormatBox.SelectedItem = FormatOf(ExportFormat.Engine);
			dialog.TryExport();
			return Task.FromResult(dialog.ErrorText.Text == "");
		};
		window.AnswerExportPath = _ => Path.Combine(folder, "layers.hqrig.json");
		window.AnswerExported = text =>
		{
			told = text;
			return Task.FromResult<string?>("OK");
		};
		Finish(window.ExportProject());

		Assert.True(File.Exists(Path.Combine(folder, "layers.hqrig.json")));
		Assert.True(File.Exists(Path.Combine(folder, "layers.atlas.png")));
		Assert.Contains("\"layers.atlas.png\"", File.ReadAllText(Path.Combine(folder, "layers.hqrig.json")));
		Assert.NotNull(told);
		Assert.Contains(folder, told);
		Assert.False(window.Exporting);
		Assert.True(window.IsEnabled);

		// The next export starts from these choices
		window.AnswerExport = dialog =>
		{
			Assert.Equal(ExportFormat.Engine, (dialog.FormatBox.SelectedItem as ExportWindow.FormatChoice)?.Format);
			Assert.True(dialog.ObjectChoiceButton.IsChecked);
			return Task.FromResult(false);
		};
		Finish(window.ExportProject());
	}

	[AvaloniaFact]
	public void The_scene_goes_out_as_pictures()
	{
		var window = WithLayersProject();
		window.Session.Scene.Duration = 0.2f;   // (six frames)
		window.Session.Scene.Loop = false;
		string folder = Path.Combine(Path.GetTempPath(), "hq-export-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		var exporter = window.RunExport(new ExportSettings
		{
			Scene = true,
			Format = ExportFormat.PngSequence,
			UseStage = false,
			Path = Path.Combine(folder, "scene.png")
		});
		Finish(exporter);
		var done = exporter.Result;
		Assert.NotNull(done);
		Assert.Equal(7, done!.FrameCount);
		Assert.Equal(7, Directory.GetFiles(folder, "scene_*.png").Length);
		Assert.True(File.Exists(Path.Combine(folder, "scene_0000.png")));
	}
}
