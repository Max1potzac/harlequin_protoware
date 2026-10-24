using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Animator.Viewer;

// File > Import (Ctrl+I): a Photoshop file, its layers as parts (with a suggested rig if you like),
// or a sprite sheet, its pieces as drawn objects or parts of the open object
public partial class MainWindow
{
	private enum ImportKind { Any, Photoshop, SpriteSheet }

	private const string PhotoshopFiles = "*.psd;*.psb";
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
				ImportKind.SpriteSheet => "Import a sprite sheet",
				_ => "Import"
			},
			Filter = kind switch
			{
				ImportKind.Photoshop => $"Photoshop files ({PhotoshopFiles})|{PhotoshopFiles}",
				ImportKind.SpriteSheet => $"Sprite sheets and pictures ({PictureFiles})|{PictureFiles}",
				_ => $"Photoshop files and sprite sheets|{PhotoshopFiles};{PictureFiles}|" +
					 $"Photoshop files ({PhotoshopFiles})|{PhotoshopFiles}|" +
					 $"Sprite sheets and pictures ({PictureFiles})|{PictureFiles}"
			}
		};
		if (_appSettings.ImportFolder is { } folder && Directory.Exists(folder)) open.InitialDirectory = folder;
		if (open.ShowDialog(this) != true) return;
		_appSettings.ImportFolder = Path.GetDirectoryName(open.FileName);
		_appSettings.Save();

		if (Path.GetExtension(open.FileName).ToLowerInvariant() is ".psd" or ".psb") ImportPhotoshop(open.FileName);
		else ImportSpriteSheet(open.FileName);
	}

	// ======================= Photoshop =======================

	private void ImportPhotoshop(string path)
	{
		string name = Path.GetFileNameWithoutExtension(path);
		PsdImportPlan plan;
		Mouse.OverrideCursor = Cursors.Wait;
		try
		{
			plan = PsdImportPlan.FromDocument(PsdReader.Read(path), name);
			_ = plan.Masks;   // the layers' shapes, for suggesting a rig: worked out while the cursor waits
		}
		catch (Exception ex)
		{
			Mouse.OverrideCursor = null;
			string why = ex is NotSupportedException or InvalidDataException or IOException
				? ex.Message
				: $"Something in it couldn't be read ({ex.GetType().Name}: {ex.Message}).";
			MessageBox.Show(this, $"{Path.GetFileName(path)} couldn't be imported.\n\n{why}", "Import a Photoshop file",
				MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		finally
		{
			Mouse.OverrideCursor = null;
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

			SceneObject? made = null;
			EditScene(() =>
			{
				made = PsdImporter.IntoScene(_scene, _assets, plan, _camera.Pan);   // in the middle of the view
				return $"Import {name}";
			});
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
		var json = ReadSheetJson(Path.ChangeExtension(path, ".json"));

		// Parts go where new parts go: on a drawn object's own bone, or on the selected bone
		// (the first one when none is selected)
		var target = Editing ? _active : null;
		var bone = target == null ? null : BoneForNewPart() ?? _skeleton.Roots.FirstOrDefault();
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
			var made = new List<Slot>();
			_state.EditStructure(() =>
			{
				made = SheetImporter.IntoObject(target, bone, _assets, plan, _state.Fps);
				return made.Count > 0 ? step : null;
			});
			if (made.Count == 0) return;
			_state.Playing = false;
			_state.SelectedSlot = made[0];
			_state.Selected = made[0].Bone;
			RefreshEverything();
		}
		else
		{
			var made = new List<SceneObject>();
			EditScene(() =>
			{
				made = SheetImporter.IntoScene(_scene, _assets, plan, _camera.Pan, _state.Fps);   // around the view's middle
				return made.Count > 0 ? step : null;
			});
			if (made.Count == 0) return;
			if (!_sceneLevel) LeaveObject();
			_objectTool.Selected = made[0];
			RefreshEverything();
		}
	}

	// The JSON a sheet program (or Harlequin's own export) wrote next to the sheet, if there's one
	private static SpriteSheetJson? ReadSheetJson(string path)
	{
		if (!File.Exists(path)) return null;
		try
		{
			var json = SpriteSheetJson.Read(File.ReadAllText(path));
			return json.Frames.Count > 0 ? json : null;
		}
		catch (Exception)
		{
			return null;   // not a sheet's JSON: the sheet gets cut some other way
		}
	}
}
