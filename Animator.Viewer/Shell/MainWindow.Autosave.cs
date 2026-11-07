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
	private Autosaver _autosave = null!;   // the bookkeeping (see Autosaver)

	private bool Unsaved => _autosave?.Unsaved ?? _state.Undo.IsDirty;   // (the title can be set before autosave starts)

	private void InitAutosave()
	{
		_autosave = new Autosaver(_session) { Post = run => Dispatcher.InvokeAsync(run) };
		_autosaveTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => AutosaveTick(), Dispatcher);
		_autosaveTimer.Start();
		Closed += (_, _) =>
		{
			_autosaveTimer?.Stop();
			ForgetRecoveryCopy(waitForIt: true);   // closing (saved or not, as asked): no copy needed
		};
	}

	// Not in the middle of something: a drag, a stroke, an export
	private void AutosaveTick() => _autosave.Tick(_appSettings.AutosaveMinutes,
		busy: Viewport.IsMouseCaptured || TimelineView.IsMouseCaptured || _paintCanvas.IsBusy || _exporting);

	// Saved, a new or another project opened, or closing: this run's copy isn't needed
	private void ForgetRecoveryCopy(bool waitForIt = false) => _autosave?.Forget(waitForIt);

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

			_autosave.Adopt(left);   // this run looks after the copy now (until it's saved), so it's not offered again
			UpdateTitle();
			return;   // one at a time: any others wait for the next start
		}
	}
}
