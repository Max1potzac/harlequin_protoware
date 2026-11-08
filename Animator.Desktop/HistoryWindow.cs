using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// Edit > History (Ctrl+Alt+Z), as the Windows editor has it: every step that can be undone,
// oldest at the top, then the ones that can be redone (greyed). Click one to go back (or on) to
// just after it; "The start" undoes everything. One window, left open beside the work: it follows
// every edit.
public sealed class HistoryWindow : Window
{
	private readonly UndoStack _undo;
	private readonly Action<int> _goTo;
	internal readonly ListBox Steps = new() { BorderThickness = new Thickness(0) };
	private bool _filling;

	public HistoryWindow(UndoStack undo, Action<int> goTo)
	{
		_undo = undo;
		_goTo = goTo;
		Title = "History";
		Width = 320;
		Height = 480;
		MinWidth = 200;
		MinHeight = 160;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		var note = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Foreground = Brushes.Gray,
			FontSize = 12,
			Margin = new Thickness(2, 0, 2, 8),
			Text = "Click a step to go back to just after it. Greyed steps can be redone; a new edit drops them."
		};
		var panel = new DockPanel { Margin = new Thickness(8) };
		DockPanel.SetDock(note, Dock.Top);
		panel.Children.Add(note);
		panel.Children.Add(Steps);
		Content = panel;

		Steps.SelectionChanged += (_, _) =>
		{
			if (_filling || Steps.SelectedIndex < 0) return;
			int to = Steps.SelectedIndex;
			Avalonia.Threading.Dispatcher.UIThread.Post(() => _goTo(to));   // (not while the list is in the middle of its own change)
		};
		KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
		_undo.Changed += Fill;
		Closed += (_, _) => _undo.Changed -= Fill;
		Fill();
		UiSize.Fit(this);
	}

	// (every edit, undo and redo)
	private void Fill()
	{
		_filling = true;
		try
		{
			var rows = new List<ListBoxItem> { Row("The start", done: true) };
			rows.AddRange(_undo.UndoNames.Select(n => Row(n, done: true)));
			rows.AddRange(_undo.RedoNames.Select(n => Row(n, done: false)));
			Steps.ItemsSource = rows;
			Steps.SelectedIndex = _undo.UndoCount;
			if (Steps.SelectedItem is { } item) Steps.ScrollIntoView(item);
		}
		finally
		{
			_filling = false;
		}
	}

	private static ListBoxItem Row(string name, bool done)
	{
		var item = new ListBoxItem { Content = name, Padding = new Thickness(6, 3) };
		if (!done)
		{
			item.Foreground = Brushes.Gray;   // (can be redone)
			item.FontStyle = FontStyle.Italic;
		}
		return item;
	}
}

// Help > Keyboard and mouse (F1), as the Windows editor has it: what the clicks, drags and keys do
// where you are (the scene, or inside an object), with the keys as they're set now. One window,
// left open beside the work; it follows along as you go in and out of objects.
public sealed class HelpWindow : Window
{
	internal readonly StackPanel Lines = new() { Margin = new Thickness(16, 12, 16, 16) };
	private string _shown = "";

	public HelpWindow()
	{
		Title = "Keyboard and mouse";
		Width = 760;
		Height = 640;
		MinWidth = 360;
		MinHeight = 240;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Content = new ScrollViewer { Content = Lines };
		KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
		UiSize.Fit(this);
	}

	// The help, a line a topic ("Viewport: …", "Timeline: …"); within a line, three spaces part its points
	public void ShowText(string text)
	{
		if (text == _shown) return;
		_shown = text;
		Lines.Children.Clear();
		foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var points = line.Split("   ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			for (int i = 0; i < points.Length; i++)
			{
				var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(i == 0 ? 0 : 14, i == 0 ? 10 : 2, 0, 0) };
				string point = points[i];
				int colon = point.IndexOf(": ", StringComparison.Ordinal);
				// "Timeline: click key = …": the topic in bold (a short one before the first colon)
				if (colon is > 0 and < 28 && !point[..colon].Contains('='))
				{
					block.Inlines!.Add(new Run(point[..(colon + 1)]) { FontWeight = FontWeight.Bold });
					block.Inlines.Add(new Run(point[(colon + 1)..]));
				}
				else block.Text = point;
				Lines.Children.Add(block);
			}
		}
	}
}
