using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Animator.Viewer;

// File > Export (Ctrl+E): PNG pictures, GIFs, sprite sheets or a video, of the scene or of one
// object's animations
public partial class MainWindow
{
	private readonly AppSettings _appSettings = AppSettings.Load();
	private ExportSettings? _lastExport;   // the dialog starts from the previous export's choices
	private bool _exporting;               // the frame loop leaves the scene alone meanwhile

	private void Export_Click(object sender, RoutedEventArgs e) => ExportProject();

	private async void ExportProject()
	{
		if (_exporting) return;
		_paint.Tool.Deactivate(_paint);   // a half-placed figure lands first, so it's in the pictures

		var dialog = new ExportDialog(_scene, _active, _state.Fps, _lastExport, _appSettings.FfmpegPath, _renderer.PixelArt)
		{
			Owner = this
		};
		if (dialog.ShowDialog() != true) return;
		var settings = dialog.Settings;
		_lastExport = settings;
		if (dialog.FfmpegPath != _appSettings.FfmpegPath)
		{
			_appSettings.FfmpegPath = dialog.FfmpegPath;
			_appSettings.Save();
		}

		// Where to: the file's name is also the start of the names of a sequence or a sheet
		var (filter, extension) = settings.Format switch
		{
			ExportFormat.Gif => ("GIF (*.gif)|*.gif", ".gif"),
			ExportFormat.SpriteSheet => ("Sprite sheet (*.png, with a .json next to it)|*.png", ".png"),
			ExportFormat.Video => ("MP4 video (*.mp4)|*.mp4", ".mp4"),
			ExportFormat.Engine => ("Harlequin rig (*.hqrig.json, with its atlas pages)|*.hqrig.json", ".hqrig.json"),
			_ => ("PNG pictures (*.png: name_0000.png, name_0001.png …)|*.png", ".png")
		};
		string suggested = settings.Scene ? DocumentName : settings.Object?.Name ?? DocumentName;
		var save = new SaveFileDialog
		{
			Filter = filter,
			DefaultExt = extension,
			FileName = suggested,
			Title = "Export to",
			OverwritePrompt = settings.Format != ExportFormat.PngSequence   // a sequence never writes that exact name
		};
		if (_appSettings.ExportFolder is { } folder && Directory.Exists(folder)) save.InitialDirectory = folder;
		if (save.ShowDialog(this) != true) return;

		settings.Path = save.FileName;
		settings.ResolveSound = ResolveSound;
		_appSettings.ExportFolder = Path.GetDirectoryName(save.FileName);
		_appSettings.Save();

		await RunExport(settings);
	}

	// Works through the export a frame at a time, letting the window breathe between frames
	private async Task RunExport(ExportSettings settings)
	{
		_state.Playing = false;
		var exporter = new Exporter(_scene, _assets, _state.Fps, settings);
		var progress = new ExportProgressWindow { Owner = this };
		progress.Show();
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
				await Dispatcher.Yield(DispatcherPriority.Background);
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
			MessageBox.Show(this, problem, "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		if (cancelled || exporter.Written.Count == 0) return;

		var (width, height) = exporter.Size;
		string first = exporter.Written[0];
		string what = exporter.Written.Count == 1 ? Path.GetFileName(first) : $"{exporter.Written.Count} files";
		var answer = MessageBox.Show(this,
			$"Exported {what}, {exporter.FrameCount} frame(s) of {width} × {height} pixels, to\n{Path.GetDirectoryName(first)}\n\nShow it in Explorer?",
			"Export", MessageBoxButton.YesNo, MessageBoxImage.Information);
		if (answer == MessageBoxResult.Yes)
		{
			try
			{
				Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true });
			}
			catch (Exception)
			{
				// No Explorer: nothing else to do
			}
		}
	}
}
