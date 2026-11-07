using Animator.Desktop.Import;
using Animator.Viewer.Import;
using Avalonia.Platform.Storage;

namespace Animator.Desktop;

// File > Import (Ctrl+I): a Photoshop file, its layers as parts (with a suggested rig if you
// like). (Animate projects and sprite sheets follow.) The reading and the importing are the
// engine's (EditorSession.Import.cs); the dialogs are this editor's.
public sealed partial class MainWindow
{
	private static readonly FilePickerFileType PhotoshopType = new("Photoshop files") { Patterns = new[] { "*.psd", "*.psb" } };

	internal async Task ImportFile()
	{
		_session.FinishPaintTool();   // a half-placed figure lands first
		var start = _settings.ImportFolder is { } folder && Directory.Exists(folder) ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
		var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Import",
			AllowMultiple = false,
			FileTypeFilter = new[] { PhotoshopType },
			SuggestedStartLocation = start
		});
		if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
		_settings.ImportFolder = Path.GetDirectoryName(path);
		try { _settings.Save(); } catch (Exception) { }
		await ImportPath(path);
	}

	// (the tests import this way, without the file picker)
	internal async Task ImportPath(string path)
	{
		string ext = Path.GetExtension(path).ToLowerInvariant();
		if (ext is ".psd" or ".psb") await ImportPhotoshop(path);
		else await Dialogs.Tell(this, "Import", $"{Path.GetFileName(path)} isn't a file this editor imports yet (Photoshop files, for now).");
	}

	// ======================= Photoshop =======================

	// What the dialog is shown with (the tests answer it); null: the user's answer
	internal Func<PsdImportWindow, Task<bool>>? AnswerPsdImport { get; set; }

	private async Task ImportPhotoshop(string path)
	{
		string name = Path.GetFileNameWithoutExtension(path);
		var plan = EditorSession.ReadPhotoshop(path, out string? why);
		if (plan == null)
		{
			await Dialogs.Tell(this, "Import a Photoshop file", $"{Path.GetFileName(path)} couldn't be imported.\n\n{why}");
			return;
		}
		using (plan)
		{
			if (plan.Parts.Count == 0)
			{
				await Dialogs.Tell(this, "Import a Photoshop file", "There's nothing to bring in: every layer of the file is empty.");
				return;
			}
			var dialog = new PsdImportWindow(plan);
			bool go = AnswerPsdImport is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
			if (!go) return;

			_camera.ViewSize = Viewport.SizeInUnits;
			var made = _session.ImportPhotoshop(plan, _camera.Pan, name);   // in the middle of the view
			if (made == null) return;
			State.Playing = false;
			EnterObject(made);
		}
	}
}
