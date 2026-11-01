using System.Windows;
using System.Windows.Controls;

namespace Animator.Viewer;

// File → Open recent (the last 10 projects; one that can't be opened any more is taken off the
// list, saying so) and File → Reopen (back to the last save, the changes since thrown away)
public partial class MainWindow
{
	private List<string> Recent => _appSettings.RecentFiles ??= new List<string>();

	// (after opening or saving a project)
	private void RememberRecent(string path)
	{
		RecentFiles.Add(Recent, path);
		_appSettings.Save();
	}

	private void FileMenu_Opened(object sender, RoutedEventArgs e)
	{
		ReopenItem.IsEnabled = _projectPath != null && File.Exists(_projectPath);
		RecentMenu.Items.Clear();
		int n = 0;
		foreach (string path in Recent.ToList())
		{
			n++;
			string number = n < 10 ? $"_{n}" : "1_0";
			var item = new MenuItem { Header = $"{number}  {Path.GetFileNameWithoutExtension(path)}", ToolTip = path };
			string chosen = path;
			item.Click += (_, _) => OpenRecent(chosen);
			RecentMenu.Items.Add(item);
		}
		if (n == 0)
		{
			RecentMenu.Items.Add(new MenuItem { Header = "(none yet)", IsEnabled = false });
			return;
		}
		RecentMenu.Items.Add(new Separator());
		var clear = new MenuItem { Header = "_Clear the list" };
		clear.Click += (_, _) =>
		{
			Recent.Clear();
			_appSettings.Save();
		};
		RecentMenu.Items.Add(clear);
	}

	private void OpenRecent(string path)
	{
		if (!File.Exists(path))
		{
			Forget(path);
			MessageBox.Show(this, $"{path}\n\nisn't there any more (moved, renamed or deleted), so it's been taken off the recent files.",
				"Open recent", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		if (!ConfirmDiscard()) return;
		if (!OpenFile(path, path, "It's been taken off the recent files.")) Forget(path);
	}

	private void Forget(string path)
	{
		if (RecentFiles.Remove(Recent, path)) _appSettings.Save();
	}

	private void Reopen_Click(object sender, RoutedEventArgs e) => Reopen();

	private void Reopen()
	{
		if (_projectPath is not { } path) return;
		if (!File.Exists(path))
		{
			MessageBox.Show(this, $"{path}\n\nisn't there any more, so there's nothing to go back to.", "Reopen",
				MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		_paint.Tool.Deactivate(_paint);
		if (Unsaved && MessageBox.Show(this, $"Go back to {DocumentName} as it was last saved? The changes since then are thrown away (undo can't bring them back).",
				"Reopen", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
			return;
		OpenFile(path, path);
	}
}
