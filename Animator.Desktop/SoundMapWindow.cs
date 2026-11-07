using Animator.Desktop.Audio;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Animator.Desktop;

// The sound map: what each event name sounds like in the editor. A list of names and files, and
// buttons to pick a file, add a name and remove one. Changes go through `edit` (undoable). The
// same as the Windows editor's.
public sealed class SoundMapWindow : Window
{
	private readonly Dictionary<string, string> _sounds;
	private readonly Func<string, Func<bool>, bool> _edit;
	private readonly Func<string, string> _stored;          // a picked file → how the map keeps it
	private readonly Func<IEnumerable<string>> _eventNames;  // names the animations use
	internal readonly ListBox List = new() { Height = 240, Margin = new Thickness(0, 0, 0, 8) };

	// What the file picker is answered with (the tests answer it); null: the user's answer
	internal Func<string, Task<string?>>? AnswerFile { get; set; }

	public SoundMapWindow(Dictionary<string, string> sounds, Func<string, Func<bool>, bool> edit, Func<string, string> stored,
		Func<IEnumerable<string>> eventNames)
	{
		_sounds = sounds;
		_edit = edit;
		_stored = stored;
		_eventNames = eventNames;
		Title = "Sound map";
		Width = 560;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		var panel = new StackPanel { Margin = new Thickness(14) };
		panel.Children.Add(new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 0, 8),
			Text = "What each event name sounds like while playing in the editor. Names the animations use but nothing plays for are listed too (silent). Paths are kept relative to the project's folder when they can be."
		});
		panel.Children.Add(List);
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
		Button Add(string text, Action click)
		{
			var b = new Button { Content = text };
			b.Click += (_, _) => click();
			buttons.Children.Add(b);
			return b;
		}
		Add("Pick a file…", () => _ = PickFile());
		Add("Add a name…", () => _ = AddName());
		Add("Remove", Remove);
		var close = Add("Close", Close);
		close.IsCancel = true;
		panel.Children.Add(buttons);
		Content = panel;
		List.DoubleTapped += (_, _) => _ = PickFile();
		Refresh();
		UiSize.Fit(this);
	}

	private string? Selected => (List.SelectedItem as ListBoxItem)?.Tag as string;

	internal IReadOnlyList<string> Rows => List.Items.OfType<ListBoxItem>().Select(i => i.Content as string ?? "").ToList();

	internal void Pick(string name) => List.SelectedItem = List.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as string) == name);

	private void Refresh()
	{
		var chosen = Selected;
		var names = _sounds.Keys.Concat(_eventNames()).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
		var items = names.Select(name => new ListBoxItem
		{
			Content = _sounds.TryGetValue(name, out var path) ? $"{name}   →   {path}" : $"{name}   (silent)",
			Tag = name
		}).ToList();
		List.ItemsSource = items;
		List.SelectedItem = items.FirstOrDefault(i => (i.Tag as string) == chosen);
	}

	internal async Task PickFile(string? name = null)
	{
		name ??= Selected;
		if (name == null) return;
		string? file = AnswerFile is { } answer ? await answer(name) : await AskForFile(name);
		if (file == null) return;
		string path = _stored(file);
		_edit($"Sound for {name}", () =>
		{
			_sounds[name] = path;
			return true;
		});
		Refresh();
	}

	private async Task<string?> AskForFile(string name)
	{
		var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = $"What \"{name}\" sounds like",
			AllowMultiple = false,
			FileTypeFilter = new[] { new FilePickerFileType("Sounds (WAV, MP3, Ogg)") { Patterns = SoundDecoder.Extensions.Select(e => "*" + e).ToArray() }, FilePickerFileTypes.All }
		});
		return files.Count > 0 ? files[0].TryGetLocalPath() : null;
	}

	private async Task AddName()
	{
		string? name = await Dialogs.AskText(this, "Sound map", "The event name (as the game calls it):", "",
			text => text.Trim().Length == 0 ? "A name." : null);
		if (name != null) await PickFile(name.Trim());
	}

	internal void Remove()
	{
		if (Selected is not { } name || !_sounds.ContainsKey(name)) return;
		_edit($"No sound for {name}", () => _sounds.Remove(name));
		Refresh();
	}
}
