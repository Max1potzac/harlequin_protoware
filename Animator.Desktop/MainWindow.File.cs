using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Animator.Desktop;

// New, open, save: the same .hqnap projects as the Windows editor (ProjectFile)
public sealed partial class MainWindow
{
	private static readonly FilePickerFileType ProjectType = new("Harlequin project")
	{
		Patterns = new[] { "*" + ProjectFile.Extension }
	};

	private bool _closingConfirmed;

	// What the new project dialog is answered with (the tests answer it); null: the user's answer
	internal Func<NewProjectWindow, Task<bool>>? AnswerNewProject { get; set; }

	// A character (the demo one, or one bone to build on), or an animation (a stage, a length,
	// and a blank background to paint)
	internal async Task NewProject()
	{
		if (!await ConfirmDiscard()) return;
		var dialog = new NewProjectWindow(State.Fps);
		bool ok = AnswerNewProject is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!ok) return;
		ClosePaintEditor();
		_assets.Clear();
		Scene scene;
		if (dialog.Kind == ProjectKind.Animation) scene = AnimationScene(dialog.StageWidth, dialog.StageHeight, dialog.LengthFrames, dialog.Fps);
		else
		{
			var (skeleton, animation) = dialog.UseDemo ? DemoCharacter.Build(_assets) : NewCharacter();
			scene = CharacterScene(skeleton, animation);
		}
		scene.Fps = dialog.Fps;
		_session.Replace(scene, null, null, sceneLevel: scene.Kind == ProjectKind.Animation);   // (an animation starts at the scene)
		AfterReplace();
	}

	// A scene to play over time: a stage, its length, and a blank background the stage's size
	// (locked: clicks on the stage go to the objects in front; open it from the list to paint)
	private Scene AnimationScene(int width, int height, int frames, int fps)
	{
		var scene = new Scene
		{
			Fps = fps,
			Kind = ProjectKind.Animation,
			StageWidth = width,
			StageHeight = height,
			Duration = frames / (float)fps,
			Loop = true
		};
		var background = SceneOps.AddDrawn(scene, "background", Vector2.Zero);
		background.Locked = true;
		var drawing = new Asset { Name = "background", Width = width, Height = height };
		_assets.AddLayer(drawing, "Base", null);   // blank, to paint
		PartOps.CreatePart(background.Skeleton, background.Skeleton.Roots[0], drawing, "background", Vector2.Zero, 0f);
		return scene;
	}

	internal async Task OpenProject()
	{
		if (!await ConfirmDiscard()) return;
		var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Open a project",
			AllowMultiple = false,
			FileTypeFilter = new[] { ProjectType }
		});
		if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
		await OpenFile(path);
	}

	// At the start: the project named on the command line
	public void OpenAtStart(string path) => Opened += async (_, _) => await OpenFile(path);

	// A project file opened in place of this one. false: it couldn't be read, and nothing changed.
	internal async Task<bool> OpenFile(string path)
	{
		if (!TryOpen(path, out string? problem))
		{
			await Dialogs.Tell(this, "Open failed", $"Couldn't open {Path.GetFileName(path)}:\n\n{problem}");
			return false;
		}
		return true;
	}

	// (without asking anything: the tests open files this way)
	internal bool TryOpen(string path, out string? problem) => TryOpen(path, out problem, path);

	// savesTo: where Save puts it (a recovery copy saves to the project it's a copy of; null: Save asks)
	internal bool TryOpen(string path, out string? problem, string? savesTo)
	{
		problem = null;
		ProjectFile.LoadedProject loaded;
		try
		{
			loaded = ProjectFile.Load(path);
		}
		catch (Exception ex)
		{
			problem = ex.Message;   // nothing was replaced yet: the current project is untouched
			return false;
		}
		// A lone character opens ready to edit; an animation, or a scene of several objects, at scene level
		_session.Replace(loaded.Scene, loaded.Store, savesTo);
		AfterReplace();
		if (savesTo != null) RememberRecent(savesTo);
		return true;
	}

	private void AfterReplace()
	{
		ForgetRecoveryCopy();   // the old document's copy isn't needed any more
		ClosePaintEditor();
		_tool.BonesEnabled = _settings.ShowBones && _session.Active?.Kind != ObjectKind.Drawn;
		_session.PoseScene(_session.ClockTime);
		ResetView();
		RefreshRigList();
		RefreshAnimationsBar();
		RefreshSkins();
		RefreshPoses();
		UpdateTitle();
	}

	internal async Task<bool> SaveProject() => _session.ProjectPath is { } path ? await SaveTo(path) : await SaveProjectAs();

	internal async Task<bool> SaveProjectAs()
	{
		var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Save the project",
			SuggestedFileName = _session.DocumentName + ProjectFile.Extension,
			DefaultExtension = ProjectFile.Extension.TrimStart('.'),
			FileTypeChoices = new[] { ProjectType },
			ShowOverwritePrompt = true
		});
		if (file?.TryGetLocalPath() is not { } path) return false;
		return await SaveTo(path);
	}

	internal async Task<bool> SaveTo(string path)
	{
		if (TrySave(path, out string? problem)) return true;
		await Dialogs.Tell(this, "Save failed", $"Couldn't save the project:\n\n{problem}");
		return false;
	}

	internal bool TrySave(string path, out string? problem)
	{
		problem = null;
		_session.FinishPaintTool();   // a half-placed figure gets saved too
		try
		{
			try
			{
				ProjectFile.KeepBackup(path, _settings.BackupCount);   // the version being replaced
			}
			catch (Exception)
			{
				// No backup (a full disk, a read-only folder): the save itself still goes ahead
			}
			_session.Save(path, thumbnail: null);
			ForgetRecoveryCopy();
			State.Undo.MarkSaved();   // (after the copy's gone: Unsaved, and so the title, follow)
			UpdateTitle();
			RememberRecent(path);
			return true;
		}
		catch (Exception ex)
		{
			problem = ex.Message;
			return false;
		}
	}

	// true: go ahead (saved, or the changes thrown away); false: cancelled
	private async Task<bool> ConfirmDiscard()
	{
		_session.FinishPaintTool();
		if (!Unsaved) return true;
		string? answer = await Dialogs.Ask(this, "Harlequin", $"Save changes to {_session.DocumentName}?", "Save", "Don't save", "Cancel");
		return answer switch
		{
			"Save" => await SaveProject(),
			"Don't save" => true,
			_ => false
		};
	}

	private async void Window_Closing(object? sender, WindowClosingEventArgs e)
	{
		if (_closingConfirmed || !Unsaved) return;
		e.Cancel = true;   // (asked first: Closing can't wait for the answer)
		if (!await ConfirmDiscard()) return;
		_closingConfirmed = true;
		Close();
	}
}
