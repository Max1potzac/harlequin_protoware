using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Windows;
using Microsoft.Win32;
using SkiaSharp;

namespace Animator.Viewer;

public partial class MainWindow
{
	// Where Save puts the project (null = never saved): the session's
	private string? _projectPath { get => _session.ProjectPath; set => _session.ProjectPath = value; }

	private string DocumentName => _session.DocumentName;

	private void UpdateTitle() =>
		Title = $"{DocumentName}{(Unsaved ? " *" : "")} — Harlequin";

	// ---------- menu ----------

	private void New_Click(object sender, RoutedEventArgs e) => NewProject();
	private void Open_Click(object sender, RoutedEventArgs e) => OpenProject();
	private void Save_Click(object sender, RoutedEventArgs e) => SaveProject();
	private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();
	private void Exit_Click(object sender, RoutedEventArgs e) => Close();   // Window_Closing asks first

	private void Window_Closing(object? sender, CancelEventArgs e)
	{
		if (!ConfirmDiscard()) e.Cancel = true;   // Cancel = keep the window open
		else RememberLayout();
	}

	// ---------- new / open ----------

	// A character (for games) or an animation (a scene to play over time): same kind of file,
	// different starting points
	private void NewProject()
	{
		if (!ConfirmDiscard()) return;
		var dialog = new NewProjectDialog(_state.Fps) { Owner = this };
		if (dialog.ShowDialog() != true) return;

		ClosePaintEditor();
		_assets.Clear();
		_state.Fps = dialog.Fps;
		var scene = dialog.Kind == ProjectKind.Animation
			? AnimationTemplate(dialog.StageWidth, dialog.StageHeight, dialog.LengthFrames)
			: CharacterTemplate(dialog.UseDemo);
		_session.Replace(scene, null, null, sceneLevel: scene.Kind == ProjectKind.Animation);   // an animation starts at the scene
		EditObject(_scene.Objects[0]);
		ResetEditorState();
	}

	// One character to open right away: the demo one, or a rig with one bone to build on.
	// A fresh rig either way, so nothing is shared with the old document.
	private Scene CharacterTemplate(bool demo)
	{
		_skeleton = new Skeleton();
		if (demo)
		{
			BuildDemoRig();
			BuildDemoAnimation();
		}
		else
		{
			_skeleton.Roots.Add(new Bone { Name = "root", Length = 60, SetupRotation = MathF.PI / 2 });   // pointing up
			_anim = new Animation { Name = "animation", Duration = 2f, Loop = true };
		}
		_state.Animations = new List<Animation> { _anim };
		_state.Animation = _anim;
		var scene = SingleObjectScene();
		scene.Kind = ProjectKind.Character;
		return scene;
	}

	// A scene to play over time: a stage, its length, and a blank background the stage's size
	private Scene AnimationTemplate(int width, int height, int frames)
	{
		var scene = new Scene
		{
			Fps = _state.Fps,
			Kind = ProjectKind.Animation,
			StageWidth = width,
			StageHeight = height,
			Duration = frames / (float)_state.Fps,
			Loop = true
		};
		var background = SceneOps.AddDrawn(scene, "background", Vector2.Zero);
		background.Locked = true;   // clicks on the stage go to the objects in front; open it from the list to paint
		var drawing = new Asset { Name = "background", Width = width, Height = height };
		_assets.AddLayer(drawing, "Base", null);   // blank, to paint
		PartOps.CreatePart(background.Skeleton, background.Skeleton.Roots[0], drawing, "background", Vector2.Zero, 0f);
		return scene;
	}

	private void OpenProject()
	{
		if (!ConfirmDiscard()) return;

		var dialog = new OpenFileDialog { Filter = ProjectFile.Filter };
		if (dialog.ShowDialog(this) != true) return;
		OpenFile(dialog.FileName, dialog.FileName);
	}

	// Opens a project file. `savesTo`: where Save puts it (a recovered copy saves over the project
	// it's a copy of; null: it asks). false: it couldn't be read, and nothing changed.
	private bool OpenFile(string path, string? savesTo, string? failNote = null)
	{
		ProjectFile.LoadedProject loaded;
		try
		{
			loaded = ProjectFile.Load(path);
		}
		catch (Exception ex)
		{
			// Nothing was replaced yet: the current project is untouched
			MessageBox.Show(this, $"Couldn't open {Path.GetFileName(path)}:\n\n{ex.Message}{(failNote != null ? "\n\n" + failNote : "")}", "Open failed",
							MessageBoxButton.OK, MessageBoxImage.Error);
			return false;
		}

		ClosePaintEditor();
		// A lone character opens ready to edit; an animation, or a scene of several objects,
		// opens at scene level
		_session.Replace(loaded.Scene, loaded.Store, savesTo);
		EditObject(_scene.Objects[0]);
		ResetEditorState();
		if (savesTo != null) RememberRecent(savesTo);
		return true;
	}

	private void ResetEditorState()
	{
		ForgetRecoveryCopy();   // the old document's copy isn't needed any more
		_session.Restart();     // nothing selected, playheads at 0, no history, nothing unsaved
		ResetView();   // an animation's stage fits the view
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
			try
			{
				ProjectFile.KeepBackup(path, _appSettings.BackupCount);   // the version being replaced
			}
			catch (Exception)
			{
				// No backup (a full disk, a read-only folder): the save itself still goes ahead
			}
			_session.Save(path, thumbnail);
			RememberRecent(path);
			ForgetRecoveryCopy();
			_state.Undo.MarkSaved();   // (again, now the recovery copy is gone: fires Undo.Changed, which refreshes the title)
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
		if (!Unsaved) return true;

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
	private SKBitmap RenderThumbnail(int size) => Snapshots.Thumbnail(_scene, _renderer, size);
}