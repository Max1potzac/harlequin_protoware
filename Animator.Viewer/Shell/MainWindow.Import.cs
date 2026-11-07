using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Animator.Viewer;

// File > Import (Ctrl+I): a Photoshop file, its layers as parts (with a suggested rig if you like),
// an Animate project, its parts and animations (with a rig worked out from them if you like), or a
// sprite sheet, its pieces as drawn objects or parts of the open object
public partial class MainWindow
{
	private enum ImportKind { Any, Photoshop, Animate, SpriteSheet }

	private const string PhotoshopFiles = "*.psd;*.psb";
	private const string AnimateFiles = "*.fla;*.xfl;DOMDocument.xml;*.zip";
	private const string PictureFiles = "*.png;*.webp;*.gif;*.bmp;*.jpg;*.jpeg";

	private void Import_Click(object sender, RoutedEventArgs e) => ImportFile();

	private void ImportFile(ImportKind kind = ImportKind.Any)
	{
		if (_exporting) return;
		_paint.Tool.Deactivate(_paint);   // a half-placed figure lands first

		var open = new OpenFileDialog
		{
			Title = kind switch
			{
				ImportKind.Photoshop => "Import a Photoshop file",
				ImportKind.Animate => "Import an Animate project (.fla, or the .xfl file in its folder)",
				ImportKind.SpriteSheet => "Import a sprite sheet",
				_ => "Import"
			},
			Filter = kind switch
			{
				ImportKind.Photoshop => $"Photoshop files ({PhotoshopFiles})|{PhotoshopFiles}",
				ImportKind.Animate => $"Animate projects (.fla, .xfl, a zipped XFL folder)|{AnimateFiles}",
				ImportKind.SpriteSheet => $"Sprite sheets and pictures ({PictureFiles})|{PictureFiles}",
				_ => $"Photoshop files, Animate projects and sprite sheets|{PhotoshopFiles};{AnimateFiles};{PictureFiles}|" +
					 $"Photoshop files ({PhotoshopFiles})|{PhotoshopFiles}|" +
					 $"Animate projects (.fla, .xfl, a zipped XFL folder)|{AnimateFiles}|" +
					 $"Sprite sheets and pictures ({PictureFiles})|{PictureFiles}"
			}
		};
		if (_appSettings.ImportFolder is { } folder && Directory.Exists(folder)) open.InitialDirectory = folder;
		if (open.ShowDialog(this) != true) return;
		_appSettings.ImportFolder = Path.GetDirectoryName(open.FileName);
		_appSettings.Save();

		string ext = Path.GetExtension(open.FileName).ToLowerInvariant();
		if (ext is ".psd" or ".psb") ImportPhotoshop(open.FileName);
		else if (ext is ".fla" or ".xfl" or ".zip" || Path.GetFileName(open.FileName).Equals("DOMDocument.xml", StringComparison.OrdinalIgnoreCase))
			ImportAnimate(open.FileName);
		else ImportSpriteSheet(open.FileName);
	}

	// ======================= Photoshop =======================

	private void ImportPhotoshop(string path)
	{
		string name = Path.GetFileNameWithoutExtension(path);
		Mouse.OverrideCursor = Cursors.Wait;
		var read = EditorSession.ReadPhotoshop(path, out string? why);
		Mouse.OverrideCursor = null;
		if (read is not { } plan)
		{
			MessageBox.Show(this, $"{Path.GetFileName(path)} couldn't be imported.\n\n{why}", "Import a Photoshop file",
				MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		using (plan)
		{
			if (plan.Parts.Count == 0)
			{
				MessageBox.Show(this, "There's nothing to bring in: every layer of the file is empty.", "Import a Photoshop file",
					MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			var dialog = new PsdImportDialog(plan) { Owner = this };
			if (dialog.ShowDialog() != true) return;

			var made = _session.ImportPhotoshop(plan, _camera.Pan, name);   // in the middle of the view
			if (made == null) return;
			_state.Playing = false;
			EnterObject(made);
		}
	}

	// ======================= Animate projects =======================

	private void ImportAnimate(string path)
	{
		string name = AnimateBringIn.ProjectName(path);
		Mouse.OverrideCursor = Cursors.Wait;
		var read = EditorSession.ReadAnimate(path, out string? why);
		Mouse.OverrideCursor = null;
		if (read is not { } plan)
		{
			MessageBox.Show(this, $"{Path.GetFileName(path)} couldn't be imported.\n\n{why}", "Import an Animate project",
				MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		using (plan)
		{
			var dialog = new XflImportDialog(plan) { Owner = this };
			if (dialog.ShowDialog() != true) return;

			SceneObject? made = null;
			Mouse.OverrideCursor = Cursors.Wait;
			try
			{
				made = _session.ImportAnimate(plan, _camera.Pan, name);   // in the middle of the view
			}
			finally
			{
				Mouse.OverrideCursor = null;
			}
			if (made == null) return;
			_state.Playing = false;
			EnterObject(made);
		}
	}

	// ======================= sprite sheets =======================

	private void ImportSpriteSheet(string path)
	{
		using var sheet = SheetImporter.LoadPicture(path);
		if (sheet == null)
		{
			MessageBox.Show(this, "That file couldn't be read as a picture.", "Import sprite sheet",
				MessageBoxButton.OK, MessageBoxImage.Error);
			return;
		}
		if (sheet.Width > SpriteSheetLayout.MaxSize || sheet.Height > SpriteSheetLayout.MaxSize)
		{
			MessageBox.Show(this, $"Sheets up to {SpriteSheetLayout.MaxSize} × {SpriteSheetLayout.MaxSize} pixels can be imported.",
				"Import sprite sheet", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		string name = Path.GetFileNameWithoutExtension(path);
		var json = EditorSession.ReadSheetJson(path);

		// Parts go where new parts go: on a drawn object's own bone, or on the selected bone
		// (the first one when none is selected)
		var target = Editing ? _active : null;
		var bone = _session.BoneForSheet();
		string partsLabel = target == null ? "As parts of an object (open one first)"
			: bone == null ? $"As parts of \"{target.Name}\" (it needs a bone first)"
			: IsDrawn ? $"As parts of \"{target.Name}\""
			: $"As parts of \"{target.Name}\", on its bone \"{bone.Name}\"";

		var dialog = new SpriteSheetDialog(sheet, name, json, partsLabel, bone != null, _renderer.PixelArt, _state.Fps)
		{
			Owner = this
		};
		if (dialog.ShowDialog() != true) return;
		_renderer.PixelArt = dialog.SharpPixels;
		var plan = dialog.Plan;
		using var keyed = plan.Sheet != sheet ? plan.Sheet : null;   // a copy with its background colour taken out
		string step = $"Import sprite sheet {name}";

		if (dialog.IntoOpenObject && target != null && bone != null)
		{
			if (_session.ImportSheetIntoObject(plan, bone, step).Count == 0) return;
			RefreshEverything();
		}
		else
		{
			var made = _session.ImportSheetIntoScene(plan, _camera.Pan, step);   // around the view's middle
			if (made.Count == 0) return;
			if (!_sceneLevel) LeaveObject();
			_objectTool.Selected = made[0];
			RefreshEverything();
		}
	}
}
