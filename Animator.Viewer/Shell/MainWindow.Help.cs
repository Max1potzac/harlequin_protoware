using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace Animator.Viewer;

// The status bar (along the bottom: the mode, what's picked, the last edit) and Help: the
// keyboard and mouse window (F1), the engine format, About. The menus' Animation entries.
public partial class MainWindow
{
	private string _helpText = "";
	private HelpWindow? _helpWindow;

	private const string EngineFormatUrl = "https://github.com/Max1potzac/harlequin_protoware/blob/master/docs/engine-format.md";

	// UpdateStatus's text: its first line in the status bar, the rest is the help
	private void ShowStatus(string text)
	{
		int newline = text.IndexOf('\n');
		string first = newline < 0 ? text : text[..newline];
		_helpText = newline < 0 ? "" : text[(newline + 1)..];
		if (Status.Text != first) Status.Text = first;
		_helpWindow?.ShowText(_helpText);
	}

	private void ShowHelp()
	{
		if (_helpWindow == null)
		{
			_helpWindow = new HelpWindow { Owner = this };
			_helpWindow.Closed += (_, _) => _helpWindow = null;
			_helpWindow.ShowText(_helpText);
			_helpWindow.Show();
		}
		else
		{
			if (_helpWindow.WindowState == WindowState.Minimized) _helpWindow.WindowState = WindowState.Normal;
			_helpWindow.Activate();
		}
	}

	private void HelpKeys_Click(object sender, RoutedEventArgs e) => ShowHelp();

	// Edit → History
	private HistoryWindow? _historyWindow;

	private void ShowHistory()
	{
		if (_historyWindow != null)
		{
			if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
			_historyWindow.Activate();
			return;
		}
		_historyWindow = new HistoryWindow(_state.Undo, count =>
		{
			_state.SelectedKeys.Clear();   // (as undo does: keys picked may not be there after)
			_state.Undo.MoveTo(count);
		}) { Owner = this };
		_historyWindow.Closed += (_, _) => _historyWindow = null;
		_historyWindow.Show();
	}

	private void HelpFormat_Click(object sender, RoutedEventArgs e) => OpenLink(EngineFormatUrl);

	private void OpenLink(string url)
	{
		try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			MessageBox.Show(this, $"Couldn't open {url}: {ex.Message}", "Harlequin", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	private void About_Click(object sender, RoutedEventArgs e)
	{
		var version = Assembly.GetExecutingAssembly().GetName().Version;
		MessageBox.Show(this,
			$"Harlequin {version?.ToString(3) ?? ""}\n\n2D skeletal and frame-by-frame animation for game assets.\n\n" +
			"Themes: Settings → Appearance (your own go in the themes folder).\nKeys and mouse buttons: Settings → Shortcuts; what they do: Help → Keyboard and mouse (F1).",
			"About Harlequin", MessageBoxButton.OK, MessageBoxImage.Information);
	}

	// ---------- the menus' Animation entries ----------

	private void AnimationMenu_Opened(object sender, RoutedEventArgs e)
	{
		AutoKeyItem.IsCheckable = true;
		AutoKeyItem.IsChecked = _state.AutoKey;
		foreach (var item in AnimationMenu.Items.OfType<System.Windows.Controls.MenuItem>())
			if (item.Tag is string id && id.StartsWith("anim.", StringComparison.Ordinal)) item.IsEnabled = !_sceneLevel;
	}

	private void SoundMapMenu_Click(object sender, RoutedEventArgs e) => OpenSoundMap();

	private void ImportEventsMenu_Click(object sender, RoutedEventArgs e)
	{
		if (_sceneLevel) Tell("Open an object first (double-click it): its animations take the events.");
		else ImportEvents();
	}

	private void ExportEventsMenu_Click(object sender, RoutedEventArgs e)
	{
		if (_sceneLevel) Tell("Open an object first (double-click it): its animations' events are written.");
		else ExportEvents();
	}

	private void Tell(string text) => MessageBox.Show(this, text, "Harlequin", MessageBoxButton.OK, MessageBoxImage.Information);

	// The play buttons at the start of the timeline bar: their Tag is the command
	private void Transport_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string id }) RunCommand(id);
		Viewport.Focus();
	}

	// (every frame) the play button shows what clicking it does
	private void SyncPlayButton()
	{
		string glyph = _state.Playing ? "" : "";
		if (PlayButton.Content as string != glyph) PlayButton.Content = glyph;
	}
}
