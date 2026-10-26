using System.Windows;
using System.Windows.Threading;

namespace Animator.Viewer;

// Autosave: every few minutes (Settings, Saving) unsaved work is copied aside, written in the
// background so drawing doesn't stall (see Recovery). If Harlequin then closes without saving,
// say in a crash, the next start offers the copy back. Saving the project itself also keeps the
// version it replaces, in a Backups folder beside it (see ProjectFile.KeepBackup).
public partial class MainWindow
{
	private DispatcherTimer? _autosaveTimer;
	private string? _recoveryFile;         // this run's copy of the open document (null: none yet)
	private DateTime _lastKept = DateTime.UtcNow;
	private Task? _keeping;                // a copy being written
	private int _documentVersion;          // goes up on every save, new and open: a copy kept for an
	                                       // older one is thrown away once it's written
	private bool _recovered;               // opened from a copy and not saved since: unsaved, whatever
	                                       // the history says
	private bool _changedSinceKept;        // edited (or undone) since the last copy: one is worth keeping

	private bool Unsaved => _state.Undo.IsDirty || _recovered;

	private void InitAutosave()
	{
		_autosaveTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => AutosaveTick(), Dispatcher);
		_autosaveTimer.Start();
		_state.Undo.Changed += () => _changedSinceKept = true;
		Closed += (_, _) =>
		{
			_autosaveTimer?.Stop();
			ForgetRecoveryCopy(waitForIt: true);   // closing (saved or not, as asked): no copy needed
		};
	}

	private void AutosaveTick()
	{
		int minutes = _appSettings.AutosaveMinutes;
		if (minutes <= 0 || _keeping != null || !Unsaved || !_changedSinceKept) return;
		if (DateTime.UtcNow - _lastKept < TimeSpan.FromMinutes(minutes)) return;
		// Not in the middle of something: a drag, a stroke, an export
		if (Viewport.IsMouseCaptured || TimelineView.IsMouseCaptured || _paintCanvas.IsBusy || _exporting) return;
		KeepRecoveryCopy();
	}

	private void KeepRecoveryCopy()
	{
		_lastKept = DateTime.UtcNow;
		_changedSinceKept = false;
		ProjectFile.Snapshot snapshot;
		try
		{
			_scene.Fps = _state.Fps;
			snapshot = ProjectFile.Take(_scene, _assets, thumbnail: null, copyPixels: true);
		}
		catch (Exception)
		{
			return;   // not being able to keep a copy isn't worth interrupting anyone for
		}

		string file = _recoveryFile ??= Recovery.NewFile(DocumentName);
		var note = Recovery.NoteForNow(_projectPath, DocumentName);
		int version = _documentVersion;
		_keeping = Task.Run(() =>
		{
			try
			{
				Recovery.Keep(file, snapshot, note);
			}
			catch (Exception)
			{
				// (the next tick tries again)
			}
			finally
			{
				snapshot.Dispose();
			}
		});
		_keeping.ContinueWith(_ => Dispatcher.InvokeAsync(() =>
		{
			_keeping = null;
			if (version != _documentVersion && file != _recoveryFile) Recovery.Remove(file);   // saved or replaced meanwhile
		}));
	}

	// Saved, a new or another project opened, or closing: this run's copy isn't needed
	private void ForgetRecoveryCopy(bool waitForIt = false)
	{
		_documentVersion++;
		_recovered = false;
		if (waitForIt) _keeping?.Wait(TimeSpan.FromSeconds(10));
		Recovery.Remove(_recoveryFile);
		_recoveryFile = null;
		_lastKept = DateTime.UtcNow;
	}

	// At the start (App.OnStartup): a copy left by a run that didn't close properly is offered back
	public void OfferRecovery()
	{
		List<Recovery.Leftover> leftovers;
		try
		{
			leftovers = Recovery.Leftovers();
		}
		catch (Exception)
		{
			return;
		}

		foreach (var left in leftovers)
		{
			string kept = left.Note.KeptUtc.ToLocalTime().ToString("g");
			var answer = MessageBox.Show(this,
				$"Harlequin didn't close properly last time. It had kept a copy of \"{left.Note.Name}\" from {kept}, with work that wasn't saved.\n\n" +
				"Yes: open that copy (then save it).\nNo: throw the copy away.\nCancel: keep it for next time.",
				"Recover unsaved work?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			if (answer == MessageBoxResult.No)
			{
				Recovery.Remove(left.File);
				continue;
			}
			if (answer != MessageBoxResult.Yes) continue;
			if (!OpenFile(left.File, left.Note.Project)) continue;

			// This run looks after the copy now (until it's saved), so it's not offered again
			_recoveryFile = left.File;
			_recovered = true;
			_changedSinceKept = false;   // the copy is what's open
			try { Recovery.WriteNote(left.File, Recovery.NoteForNow(left.Note.Project, left.Note.Name)); }
			catch (Exception) { }
			UpdateTitle();
			return;   // one at a time: any others wait for the next start
		}
	}
}
