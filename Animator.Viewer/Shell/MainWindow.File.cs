using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Windows;
using Microsoft.Win32;
using SkiaSharp;

namespace Animator.Viewer;

public partial class MainWindow
{
	private string? _projectPath;   // null = never saved

	private string DocumentName =>
		_projectPath == null ? "Untitled" : Path.GetFileNameWithoutExtension(_projectPath);

	private void UpdateTitle() =>
		Title = $"{DocumentName}{(_state.Undo.IsDirty ? " *" : "")} — Harlequin";

	// ---------- menu ----------

	private void New_Click(object sender, RoutedEventArgs e) => NewProject();
	private void Open_Click(object sender, RoutedEventArgs e) => OpenProject();
	private void Save_Click(object sender, RoutedEventArgs e) => SaveProject();
	private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();
	private void Exit_Click(object sender, RoutedEventArgs e) => Close();   // Window_Closing asks first

	private void Window_Closing(object? sender, CancelEventArgs e)
	{
		if (!ConfirmDiscard()) e.Cancel = true;   // Cancel = keep the window open
	}

	// ---------- new / open ----------

	private void NewProject()
	{
		if (!ConfirmDiscard()) return;
		ClosePaintEditor();
		_assets.Clear();

		// "New" starts from the demo character, as the only object (a fresh rig, so nothing
		// is shared with the old document)
		_skeleton = new Skeleton();
		BuildDemoRig();
		BuildDemoAnimation();
		_state.Animations = new List<Animation> { _anim };
		_state.Animation = _anim;
		_scene = SingleObjectScene();
		_active = null;
		_sceneLevel = false;
		EditObject(_scene.Objects[0]);

		_projectPath = null;
		ResetEditorState();
	}

	private void OpenProject()
	{
		if (!ConfirmDiscard()) return;

		var dialog = new OpenFileDialog { Filter = ProjectFile.Filter };
		if (dialog.ShowDialog(this) != true) return;

		ProjectFile.LoadedProject loaded;
		try
		{
			loaded = ProjectFile.Load(dialog.FileName);
		}
		catch (Exception ex)
		{
			// Nothing was replaced yet: the current project is untouched
			MessageBox.Show(this, $"Couldn't open the project:\n\n{ex.Message}", "Open failed",
							MessageBoxButton.OK, MessageBoxImage.Error);
			return;
		}

		ClosePaintEditor();
		_scene = loaded.Scene;
		_assets.ReplaceWith(loaded.Store);
		_state.Fps = _scene.Fps;
		_active = null;
		EditObject(_scene.Objects[0]);
		// A lone character opens ready to edit; a scene of several objects opens at scene level
		_sceneLevel = _scene.Objects.Count > 1;

		_projectPath = dialog.FileName;
		ResetEditorState();
	}

	private void ResetEditorState()
	{
		_state.Selected = null;
		_state.SelectedSlot = null;
		_state.SelectedKeys.Clear();
		_state.Time = 0;
		_sceneTime = 0;
		_objectTool.Selected = null;
		_state.Playing = false;
		_camera.Reset();
		_state.Undo.Clear();       // the history belongs to the previous document
		_state.Undo.MarkSaved();   // a freshly opened document has no unsaved changes
		UpdateStatus();
	}

	// ---------- save ----------

	private bool SaveProject() => _projectPath == null ? SaveProjectAs() : SaveTo(_projectPath);

	private bool SaveProjectAs()
	{
		var dialog = new SaveFileDialog
		{
			Filter = ProjectFile.Filter,
			DefaultExt = ProjectFile.Extension,
			FileName = DocumentName
		};
		if (dialog.ShowDialog(this) != true) return false;
		return SaveTo(dialog.FileName);
	}

	private bool SaveTo(string path)
	{
		_paint.Tool.Deactivate(_paint);   // finish a half-placed figure, so it gets saved too
		try
		{
			using var thumbnail = RenderThumbnail(256);
			_scene.Fps = _state.Fps;
			ProjectFile.Save(path, _scene, _assets, thumbnail);
			_projectPath = path;
			_state.Undo.MarkSaved();   // fires Undo.Changed, which refreshes the title
			return true;
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, $"Couldn't save the project:\n\n{ex.Message}", "Save failed",
							MessageBoxButton.OK, MessageBoxImage.Error);
			return false;
		}
	}

	// true = go ahead (saved or discarded), false = the user cancelled
	private bool ConfirmDiscard()
	{
		_paint.Tool.Deactivate(_paint);
		if (!_state.Undo.IsDirty) return true;

		var answer = MessageBox.Show(this, $"Save changes to {DocumentName}?", "Harlequin",
									 MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
		return answer switch
		{
			MessageBoxResult.Yes => SaveProject(),
			MessageBoxResult.No => true,
			_ => false
		};
	}

	// A small picture of the scene as it is now, framed around every visible object
	private SKBitmap RenderThumbnail(int size)
	{
		var bmp = new SKBitmap(size, size);
		using var canvas = new SKCanvas(bmp);
		canvas.Clear(new SKColor(40, 44, 52));

		var shown = _scene.Objects.Where(o => o.Visible).ToList();
		var points = new List<Vector2>();
		foreach (var obj in shown)
		{
			var box = ObjectGizmo.LocalBounds(obj, _renderer);   // its parts and bones
			var toScene = _scene.WorldTransform(obj);
			foreach (var corner in new[] { new Vector2(box.Left, box.Top), new Vector2(box.Right, box.Top),
										   new Vector2(box.Right, box.Bottom), new Vector2(box.Left, box.Bottom) })
				points.Add(Vector2.Transform(corner, toScene));
		}
		if (points.Count == 0) return bmp;

		var min = points.Aggregate(Vector2.Min);
		var max = points.Aggregate(Vector2.Max);
		var center = (min + max) / 2;
		float extent = MathF.Max(max.X - min.X, max.Y - min.Y) + 40;   // a little margin
		float scale = size / extent;

		canvas.Translate(size / 2f, size / 2f);
		canvas.Scale(scale, -scale);   // y-up, like the viewport
		canvas.Translate(-center.X, -center.Y);
		foreach (var obj in shown)
		{
			var m = SkeletonRenderer.ToSk(_scene.WorldTransform(obj));
			canvas.Save();
			canvas.Concat(in m);
			_renderer.DrawImages(canvas, obj.Skeleton);
			canvas.Restore();
		}
		return bmp;
	}
}