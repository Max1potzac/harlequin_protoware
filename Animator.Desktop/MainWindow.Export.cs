using System.Diagnostics;
using Animator.Viewer.Export;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Animator.Desktop;

// File > Export (Ctrl+E): PNG pictures, GIFs, sprite sheets, a video or a game engine's rig, of
// the scene or of one object's animations. The writing is the engine's (Exporter); the dialogs
// are this editor's.
public sealed partial class MainWindow
{
	private ExportSettings? _lastExport;   // the dialog starts from the previous export's choices
	private bool _exporting;               // the frame loop leaves the scene alone meanwhile

	// What the dialogs are answered with (the tests answer them); null: the user's answer
	internal Func<ExportWindow, Task<bool>>? AnswerExport { get; set; }
	internal Func<ExportSettings, string?>? AnswerExportPath { get; set; }
	internal Func<string, Task<string?>>? AnswerExported { get; set; }

	internal bool Exporting => _exporting;

	internal async Task ExportProject()
	{
		if (_exporting) return;
		_session.FinishPaintTool();   // a half-placed figure lands first, so it's in the pictures

		var dialog = new ExportWindow(Scene, _session.Active, State.Fps, _lastExport, _settings.FfmpegPath, _renderer.PixelArt);
		bool go = AnswerExport is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!go) return;
		var settings = dialog.Settings;
		_lastExport = settings;
		if (dialog.FfmpegPath != _settings.FfmpegPath)
		{
			_settings.FfmpegPath = dialog.FfmpegPath;
			try { _settings.Save(); } catch (Exception) { }
		}

		string? path = AnswerExportPath is { } where ? where(settings) : await PickExportPath(settings);
		if (path == null) return;
		settings.Path = path;
		_settings.ExportFolder = Path.GetDirectoryName(path);
		try { _settings.Save(); } catch (Exception) { }

		if (await RunExport(settings) is not { } exporter) return;

		var (width, height) = exporter.Size;
		string first = exporter.Written[0];
		string what = exporter.Written.Count == 1 ? Path.GetFileName(first) : $"{exporter.Written.Count} files";
		string text = $"Exported {what}, {exporter.FrameCount} frame(s) of {width} × {height} pixels, to\n{Path.GetDirectoryName(first)}";
		string? reply = AnswerExported is { } done ? await done(text) : await Dialogs.Ask(this, "Export", text, "Show it", "OK");
		if (reply == "Show it") await ShowInFolder(first);
	}

	// Where to: the file's name is also the start of the names of a sequence or a sheet
	private async Task<string?> PickExportPath(ExportSettings settings)
	{
		var (type, extension) = settings.Format switch
		{
			ExportFormat.Gif => (new FilePickerFileType("GIF") { Patterns = new[] { "*.gif" } }, ".gif"),
			ExportFormat.SpriteSheet => (new FilePickerFileType("Sprite sheet (.png, with a .json next to it)") { Patterns = new[] { "*.png" } }, ".png"),
			ExportFormat.Video => (new FilePickerFileType("MP4 video") { Patterns = new[] { "*.mp4" } }, ".mp4"),
			ExportFormat.Engine => (new FilePickerFileType("Harlequin rig (.hqrig.json, with its atlas pages)") { Patterns = new[] { "*.hqrig.json" } }, ".hqrig.json"),
			_ => (new FilePickerFileType("PNG pictures (name_0000.png, name_0001.png …)") { Patterns = new[] { "*.png" } }, ".png")
		};
		string suggested = (settings.Scene ? _session.DocumentName : settings.Object?.Name ?? _session.DocumentName) + extension;
		var start = _settings.ExportFolder is { } folder && Directory.Exists(folder) ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
		var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Export to",
			SuggestedFileName = suggested,
			DefaultExtension = extension.TrimStart('.'),
			FileTypeChoices = new[] { type },
			SuggestedStartLocation = start,
			ShowOverwritePrompt = settings.Format != ExportFormat.PngSequence   // a sequence never writes that exact name
		});
		if (file?.TryGetLocalPath() is not { } path) return null;
		if (Path.GetExtension(path).Length == 0) path += extension;   // (a name typed without one)
		return path;
	}

	// Works through the export a step at a time, letting the window breathe between steps. The
	// exporter when it finished (null: it failed, and that was said, or it was cancelled).
	internal async Task<Exporter?> RunExport(ExportSettings settings)
	{
		State.Playing = false;
		settings.ResolveSound = ResolveSound;
		var exporter = new Exporter(Scene, _assets, State.Fps, settings);
		var progress = new ExportProgressWindow();
		progress.Show(this);
		IsEnabled = false;
		_exporting = true;

		string? problem = null;
		bool cancelled = false;
		try
		{
			using var steps = exporter.Run().GetEnumerator();
			while (true)
			{
				bool more;
				try
				{
					more = steps.MoveNext();
				}
				catch (Exception ex)
				{
					problem = ex.Message;   // what was written so far is removed again
					break;
				}
				if (!more) break;
				progress.Report(steps.Current);
				if (progress.Cancelled)
				{
					cancelled = true;
					break;   // disposing the steps removes the files written so far
				}
				await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
			}
		}
		finally
		{
			_exporting = false;
			IsEnabled = true;
			progress.Finish();
			Activate();
		}

		if (problem != null)
		{
			await Dialogs.Tell(this, "Export", problem);
			return null;
		}
		return cancelled || exporter.Written.Count == 0 ? null : exporter;
	}

	// A path the sound map (or a sound track) keeps → the file
	private string ResolveSound(string stored) =>
		Path.IsPathRooted(stored) ? stored : Path.GetFullPath(Path.Combine(_session.ProjectFolder ?? Environment.CurrentDirectory, stored));

	// The file in its folder: picked in Explorer on Windows, in the Finder on a Mac, the folder
	// opened elsewhere
	private async Task ShowInFolder(string file)
	{
		try
		{
			if (OperatingSystem.IsWindows())
				Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
			else if (OperatingSystem.IsMacOS())
				Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", file } });
			else if (Path.GetDirectoryName(file) is { } folder)
				await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
		}
		catch (Exception)
		{
			// Nothing to show it with: nothing else to do
		}
	}
}
