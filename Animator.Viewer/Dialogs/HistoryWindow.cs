using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Animator.Viewer.Dialogs;

// Edit → History: every step that can be undone, oldest at the top, then the ones that can be
// redone (greyed). Click one to go back (or on) to just after it; "The start" undoes everything.
// One window, left open beside the work: it follows every edit. Built in code (no XAML).
public sealed class HistoryWindow : Window
{
	private readonly UndoStack _undo;
	private readonly Action<int> _goTo;
	private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
	private bool _filling;

	public HistoryWindow(UndoStack undo, Action<int> goTo)
	{
		_undo = undo;
		_goTo = goTo;
		Title = "History";
		Width = 300;
		Height = 460;
		MinWidth = 200;
		MinHeight = 160;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		SetResourceReference(BackgroundProperty, Themes.Key("Window"));
		SetResourceReference(ForegroundProperty, Themes.Key("Text"));

		var panel = new DockPanel { Margin = new Thickness(8) };
		var note = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(2, 0, 2, 8),
			Text = "Click a step to go back to just after it. Greyed steps can be redone; a new edit drops them."
		};
		note.SetResourceReference(TextBlock.ForegroundProperty, Themes.Key("TextMuted"));
		DockPanel.SetDock(note, Dock.Top);
		panel.Children.Add(note);
		panel.Children.Add(_list);
		Content = panel;

		_list.SelectionChanged += (_, _) =>
		{
			if (_filling || _list.SelectedIndex < 0) return;
			_goTo(_list.SelectedIndex);
		};
		_list.PreviewKeyDown += (_, e) =>
		{
			if (e.Key == Key.Escape) Close();
		};
		_undo.Changed += Fill;
		Closed += (_, _) => _undo.Changed -= Fill;
		Fill();
	}

	// (every edit, undo and redo)
	private void Fill()
	{
		_filling = true;
		try
		{
			_list.Items.Clear();
			_list.Items.Add(Row("The start", done: true));
			foreach (string name in _undo.UndoNames) _list.Items.Add(Row(name, done: true));
			foreach (string name in _undo.RedoNames) _list.Items.Add(Row(name, done: false));
			_list.SelectedIndex = _undo.UndoCount;
			_list.ScrollIntoView(_list.SelectedItem);
		}
		finally { _filling = false; }
	}

	private static ListBoxItem Row(string name, bool done)
	{
		var item = new ListBoxItem { Content = name, Padding = new Thickness(6, 3, 6, 3) };
		if (!done)
		{
			item.SetResourceReference(ForegroundProperty, Themes.Key("TextDisabled"));
			item.FontStyle = FontStyles.Italic;
		}
		return item;
	}
}
