using Animator.Desktop.Import;
using Animator.Core.Export;
using Animator.Viewer.Import;
using Avalonia.Platform.Storage;

namespace Animator.Desktop;

// File > Import (Ctrl+I): a Photoshop file, its layers as parts (with a suggested rig if you like),
// an Animate project, its parts and animations (with a rig worked out from them if you like), or
// a sprite sheet, its pieces as drawn objects or parts of the open object. The reading and the
// importing are the engine's (EditorSession.Import.cs); the dialogs are this editor's.
public sealed partial class MainWindow
{
	private static readonly FilePickerFileType PhotoshopType = new("Photoshop files") { Patterns = new[] { "*.psd", "*.psb" } };
	private static readonly FilePickerFileType AnimateType = new("Animate projects (.fla, .xfl, a zipped XFL folder)") { Patterns = new[] { "*.fla", "*.xfl", "DOMDocument.xml", "*.zip" } };
	private static readonly FilePickerFileType PictureType = new("Sprite sheets and pictures") { Patterns = new[] { "*.png", "*.webp", "*.gif", "*.bmp", "*.jpg", "*.jpeg" } };
	private static readonly FilePickerFileType AnyImportType = new("Photoshop files, Animate projects and sprite sheets")
	{
		Patterns = PhotoshopType.Patterns!.Concat(AnimateType.Patterns!).Concat(PictureType.Patterns!).ToArray()
	};

	internal async Task ImportFile()
	{
		_session.FinishPaintTool();   // a half-placed figure lands first
		var start = _settings.ImportFolder is { } folder && Directory.Exists(folder) ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
		var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Import",
			AllowMultiple = false,
			FileTypeFilter = new[] { AnyImportType, PhotoshopType, AnimateType, PictureType },
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
		else if (ext is ".fla" or ".xfl" or ".zip" || Path.GetFileName(path).Equals("DOMDocument.xml", StringComparison.OrdinalIgnoreCase)) await ImportAnimate(path);
		else await ImportSpriteSheet(path);
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

	// ======================= Animate projects =======================

	internal Func<XflImportWindow, Task<bool>>? AnswerAnimateImport { get; set; }

	private async Task ImportAnimate(string path)
	{
		string name = AnimateBringIn.ProjectName(path);
		var plan = EditorSession.ReadAnimate(path, out string? why);
		if (plan == null)
		{
			await Dialogs.Tell(this, "Import an Animate project", $"{Path.GetFileName(path)} couldn't be imported.\n\n{why}");
			return;
		}
		using (plan)
		{
			if (plan.Timelines.Count == 0)
			{
				await Dialogs.Tell(this, "Import an Animate project", "There's nothing to bring in: the project has no timeline with anything on it.");
				return;
			}
			var dialog = new XflImportWindow(plan);
			bool go = AnswerAnimateImport is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
			if (!go) return;

			_camera.ViewSize = Viewport.SizeInUnits;
			SceneObject? made;
			try
			{
				made = _session.ImportAnimate(plan, _camera.Pan, name);   // in the middle of the view
			}
			catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException)
			{
				await Dialogs.Tell(this, "Import an Animate project", $"{Path.GetFileName(path)} couldn't be imported.\n\n{ex.Message}");
				return;
			}
			if (made == null) return;
			State.Playing = false;
			EnterObject(made);
		}
	}

	// ======================= sprite sheets =======================

	internal Func<SpriteSheetWindow, Task<bool>>? AnswerSheetImport { get; set; }

	private async Task ImportSpriteSheet(string path)
	{
		using var sheet = SheetImporter.LoadPicture(path);
		if (sheet == null)
		{
			await Dialogs.Tell(this, "Import a sprite sheet", "That file couldn't be read as a picture.");
			return;
		}
		if (sheet.Width > SpriteSheetLayout.MaxSize || sheet.Height > SpriteSheetLayout.MaxSize)
		{
			await Dialogs.Tell(this, "Import a sprite sheet", $"Sheets up to {SpriteSheetLayout.MaxSize} × {SpriteSheetLayout.MaxSize} pixels can be imported.");
			return;
		}

		string name = Path.GetFileNameWithoutExtension(path);
		var json = EditorSession.ReadSheetJson(path);

		// Parts go where new parts go: on a drawn object's own bone, or on the selected bone
		var target = Editing ? _session.Active : null;
		var bone = _session.BoneForSheet();
		string partsLabel = target == null ? "As parts of an object (open one first)"
			: bone == null ? $"As parts of \"{target.Name}\" (it needs a bone first)"
			: target.Kind == ObjectKind.Drawn ? $"As parts of \"{target.Name}\""
			: $"As parts of \"{target.Name}\", on its bone \"{bone.Name}\"";

		var dialog = new SpriteSheetWindow(sheet, name, json, partsLabel, bone != null, _renderer.PixelArt, State.Fps);
		bool go = AnswerSheetImport is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!go) return;
		_renderer.PixelArt = dialog.SharpPixels;
		var plan = dialog.Plan;
		using var keyed = plan.Sheet != sheet ? plan.Sheet : null;   // a copy with its background colour taken out
		string step = $"Import sprite sheet {name}";

		if (dialog.IntoOpenObject && target != null && bone != null)
		{
			if (_session.ImportSheetIntoObject(plan, bone, step).Count == 0) return;
		}
		else
		{
			_camera.ViewSize = Viewport.SizeInUnits;
			var made = _session.ImportSheetIntoScene(plan, _camera.Pan, step);   // around the view's middle
			if (made.Count == 0) return;
			if (!SceneLevel) LeaveObject();
			_objectTool.Selected = made[0];
		}
		RefreshRigList();
		RefreshAnimationsBar();
	}
}
