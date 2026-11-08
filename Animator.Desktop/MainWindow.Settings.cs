using Avalonia.Media;
using Avalonia.Threading;

namespace Animator.Desktop;

// File > Settings (Ctrl+,): keys, mouse buttons, saving, the look; and autosave, which keeps a
// recovery copy of unsaved work every few minutes (written in the background, see Recovery) so
// the next start can offer it back after a crash. Saving over a project keeps the version it
// replaces in a Backups folder beside it (ProjectFile.KeepBackup).
public sealed partial class MainWindow
{
	private DispatcherTimer? _autosaveTimer;
	private Autosaver _autosave = null!;

	// What the settings dialog is answered with (the tests answer it); null: the user's answer
	internal Func<SettingsWindow, Task<bool>>? AnswerSettings { get; set; }

	internal AppSettings Settings => _settings;

	// Unsaved: changed since the last save, or opened from a recovery copy and not saved since
	private bool Unsaved => _autosave?.Unsaved ?? State.Undo.IsDirty;

	internal async Task OpenSettings()
	{
		_session.FinishPaintTool();
		var dialog = new SettingsWindow(_keys, HasCommand, _tool.SelectButton, _tool.TransformButton, _tool.PanButton,
			_settings.AutosaveMinutes, _settings.BackupCount, _settings.Theme);
		bool ok = AnswerSettings is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!ok) return;

		_keys = dialog.Keys;
		_settings.Shortcuts = _keys.Changes();
		_settings.SelectButton = dialog.SelectButton;
		_settings.TransformButton = dialog.TransformButton;
		_settings.PanButton = dialog.PanButton;
		_settings.AutosaveMinutes = dialog.AutosaveMinutes;
		_settings.BackupCount = dialog.BackupCount;
		_settings.Theme = dialog.ThemeName;
		_settings.UiScale = dialog.Scale;
		try { _settings.Save(); } catch (Exception) { }   // (not being able to remember it isn't worth interrupting anyone for)
		ApplySettings();
		ShowKeysInMenus();
		ShowPaintToolKeys();
	}

	private bool LightTheme => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;

	// The colours of this window's own that go with the theme (the controls follow it themselves)
	private void ShowThemeColours()
	{
		bool fluent = Themes.Current == Themes.Dark || Themes.Current == Themes.Light;   // (Fluent's own look: its window colour)
		Background = new SolidColorBrush(!fluent ? Themes.ColorOf("Window") : LightTheme ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x1E, 0x21, 0x27));
		if (Content != null) RefreshSkins();   // (their rows' colours, once they're there)
	}

	// The look the settings ask for (at the start)
	private void ApplyAppearance()
	{
		SettingsWindow.ApplyTheme(_settings.Theme);
		UiSize.Set(_settings.UiScale);
	}

	// ======================= autosave =======================

	private void InitAutosave()
	{
		_autosave = new Autosaver(_session) { Post = run => Dispatcher.UIThread.Post(run) };
		_autosaveTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => AutosaveTick());
		_autosaveTimer.Start();
		Closed += (_, _) =>
		{
			_autosaveTimer?.Stop();
			ForgetRecoveryCopy(waitForIt: true);   // closing (saved or not, as asked): no copy needed
		};
	}

	// Not in the middle of something: a drag, a stroke, an export
	internal bool AutosaveTick() => _autosave.Tick(_settings.AutosaveMinutes,
		busy: _viewPressed || _timelinePressed || PaintBusy || _exporting);

	internal Autosaver Autosave => _autosave;

	// Saved, a new or another project opened, or closing: this run's copy isn't needed
	private void ForgetRecoveryCopy(bool waitForIt = false) => _autosave?.Forget(waitForIt);

	// What the recovery question is answered with (the tests answer it): "Open", "Throw it away" or
	// "Keep it" (null: the user's answer)
	internal Func<Recovery.Leftover, Task<string?>>? AnswerRecovery { get; set; }

	// At the start: a copy left by a run that didn't close properly is offered back
	internal async Task OfferRecovery()
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
			string? answer = AnswerRecovery is { } answering ? await answering(left) : await Dialogs.Ask(this, "Recover unsaved work?",
				$"Harlequin didn't close properly last time. It had kept a copy of \"{left.Note.Name}\" from {kept}, with work that wasn't saved.\n\n" +
				"Open: open that copy (then save it).\nThrow it away: the copy is deleted.\nKeep it: ask again next time.",
				"Open", "Throw it away", "Keep it");
			if (answer == "Throw it away")
			{
				Recovery.Remove(left.File);
				continue;
			}
			if (answer != "Open") continue;
			if (!TryOpen(left.File, out string? problem, savesTo: left.Note.Project))
			{
				await Dialogs.Tell(this, "Open failed", $"The copy couldn't be opened:\n\n{problem}");
				continue;
			}
			_autosave.Adopt(left);   // this run looks after the copy now (until it's saved), so it's not offered again
			UpdateTitle();
			return;   // one at a time: any others wait for the next start
		}
	}
}
