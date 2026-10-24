using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Animator.Viewer;

// File > Import sprite sheet (Ctrl+I): a sheet's pieces as drawn objects, or as parts of the
// open object. Pieces that make an animation become one part showing them frame by frame.
public partial class MainWindow
{
	private void ImportSheet_Click(object sender, RoutedEventArgs e) => ImportSpriteSheet();

	private void ImportSpriteSheet()
	{
		if (_exporting) return;
		_paint.Tool.Deactivate(_paint);   // a half-placed figure lands first

		var open = new OpenFileDialog
		{
			Title = "Import a sprite sheet",
			Filter = "Pictures (*.png;*.webp;*.gif;*.bmp;*.jpg;*.jpeg)|*.png;*.webp;*.gif;*.bmp;*.jpg;*.jpeg"
		};
		if (_appSettings.ImportFolder is { } folder && Directory.Exists(folder)) open.InitialDirectory = folder;
		if (open.ShowDialog(this) != true) return;
		_appSettings.ImportFolder = Path.GetDirectoryName(open.FileName);
		_appSettings.Save();

		using var sheet = SheetImporter.LoadPicture(open.FileName);
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

		string name = Path.GetFileNameWithoutExtension(open.FileName);
		var json = ReadSheetJson(Path.ChangeExtension(open.FileName, ".json"));

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
