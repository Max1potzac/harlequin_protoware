using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Animator.Viewer.Dialogs;

// The sound map: what each event name sounds like in the editor. Built in code (no XAML): a list
// of names and files, and buttons to add, change and remove. Changes go through `edit` (undoable).
public sealed class SoundMapWindow : Window
{
	private readonly Dictionary<string, string> _sounds;
	private readonly Func<string, Func<bool>, bool> _edit;
	private readonly Func<string, string> _stored;          // a picked file → how the map keeps it
	private readonly Func<IEnumerable<string>> _eventNames;  // names the animations use
	private readonly ListBox _list = new() { Height = 220, Margin = new Thickness(0, 0, 0, 8) };

	public SoundMapWindow(Dictionary<string, string> sounds, Func<string, Func<bool>, bool> edit, Func<string, string> stored,
		Func<IEnumerable<string>> eventNames)
	{
		_sounds = sounds;
		_edit = edit;
		_stored = stored;
		_eventNames = eventNames;
		Title = "Sound map";
		Width = 520;
		SizeToContent = SizeToContent.Height;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		SetResourceReference(BackgroundProperty, Themes.Key("Window"));
		SetResourceReference(ForegroundProperty, Themes.Key("Text"));

		var panel = new StackPanel { Margin = new Thickness(14) };
		panel.Children.Add(new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 8),
			Text = "What each event name sounds like while playing in the editor. Names the animations use but nothing plays for are listed too (silent). Paths are kept relative to the project's folder when they can be."
		});
		panel.Children.Add(_list);
		var buttons = new WrapPanel();
		Button Add(string text, RoutedEventHandler click)
		{
			var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
			b.Click += click;
			buttons.Children.Add(b);
			return b;
		}
		Add("Pick a file…", (_, _) => PickFile());
		Add("Add a name…", (_, _) => AddName());
		Add("Remove", (_, _) => Remove());
		var close = Add("Close", (_, _) => Close());
		close.IsCancel = true;
		panel.Children.Add(buttons);
		Content = panel;
		_list.MouseDoubleClick += (_, _) => PickFile();
		Refresh();
	}

	private string? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as string;

	private void Refresh()
	{
		var chosen = Selected;
		_list.Items.Clear();
		var names = _sounds.Keys.Concat(_eventNames()).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
		foreach (var name in names)
		{
			var item = new ListBoxItem
			{
				Content = _sounds.TryGetValue(name, out var path) ? $"{name}   →   {path}" : $"{name}   (silent)",
				Tag = name
			};
			_list.Items.Add(item);
			if (name == chosen) _list.SelectedItem = item;
		}
	}

	private void PickFile(string? name = null)
	{
		name ??= Selected;
		if (name == null) return;
		var dialog = new OpenFileDialog
		{
			Title = $"What \"{name}\" sounds like",
			Filter = "Sounds (*.wav;*.mp3;*.wma;*.m4a;*.aac)|*.wav;*.mp3;*.wma;*.m4a;*.aac|All files (*.*)|*.*"
		};
		if (dialog.ShowDialog(this) != true) return;
		string path = _stored(dialog.FileName);
		_edit($"Sound for {name}", () =>
		{
			_sounds[name] = path;
			return true;
		});
		Refresh();
	}

	private void AddName()
	{
		var dialog = new InputDialog("Sound map", "The event name (as the game calls it):", "",
			text => text.Trim().Length == 0 ? "A name." : null) { Owner = this };
		if (dialog.ShowDialog() != true) return;
		PickFile(dialog.Answer.Trim());
	}

	private void Remove()
	{
		if (Selected is not { } name || !_sounds.ContainsKey(name)) return;
		_edit($"No sound for {name}", () => _sounds.Remove(name));
		Refresh();
	}
}
