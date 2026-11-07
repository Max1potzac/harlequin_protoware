using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace Animator.Desktop;

// Settings: which key does what (paint keys too), which mouse button picks, which moves things
// and which moves the view, how work is kept safe (autosave, backups), and how the editor looks.
// The same as the Windows editor's dialog. Nothing changes until OK (the theme and size show at
// once, and go back on Cancel). ShowDialog<bool>: true, OK.
public sealed class SettingsWindow : Window
{
	private sealed record ButtonChoice(PointerButton Button, string Label)
	{
		public override string ToString() => Label;
	}

	// A number to pick, as the list shows it
	private sealed record Choice(int Value, string Label)
	{
		public override string ToString() => Label;
	}

	private sealed record ThemeChoice(string Name, ThemeVariant Variant)
	{
		public override string ToString() => Name;
	}

	private static readonly ButtonChoice[] Buttons =
	{
		new(PointerButton.Left, "Left button"),
		new(PointerButton.Right, "Right button"),
		new(PointerButton.Middle, "Middle button (wheel)"),
		new(PointerButton.XButton1, "Side button (back)"),
		new(PointerButton.XButton2, "Side button (forward)")
	};

	internal static readonly (string Name, ThemeVariant Variant)[] ThemeNames = { ("Dark", ThemeVariant.Dark), ("Light", ThemeVariant.Light) };

	private static readonly int[] AutosaveMinuteChoices = { 0, 1, 2, 5, 10, 15, 30 };
	private static readonly int[] BackupChoices = { 0, 1, 2, 3, 5, 10, 20 };

	private readonly Func<string, bool> _shown;   // the commands this editor has
	private KeyChord? _captured;
	private readonly string _themeBefore;
	private readonly double _sizeBefore = UiSize.Current;
	private bool _ok;

	// What OK leaves: the keys, the buttons, saving, the look
	public KeyMap Keys { get; }
	public PointerButton SelectButton { get; private set; }
	public PointerButton TransformButton { get; private set; }
	public PointerButton PanButton { get; private set; }
	public int AutosaveMinutes { get; private set; }   // 0: never
	public int BackupCount { get; private set; }       // 0: none
	public string ThemeName { get; private set; }
	public double Scale { get; private set; } = UiSize.Current;

	internal readonly TabControl Tabs = new();
	internal readonly TextBox FilterBox = new() { Watermark = "Find a command or a key" };
	internal readonly ListBox CommandList = new();
	internal readonly TextBlock CommandTitle = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
	internal readonly TextBlock CommandKeys = new() { TextWrapping = TextWrapping.Wrap };
	internal readonly TextBox CaptureBox = new() { IsReadOnly = true, Width = 200, Watermark = "Click here, then press the keys" };
	internal readonly TextBlock ConflictText = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
	internal readonly Button SetButton = new() { Content = "Set" };
	internal readonly Button AddButton = new() { Content = "Add" };
	internal readonly Button ClearButton = new() { Content = "No key" };
	internal readonly Button DefaultButton = new() { Content = "As at first" };
	internal readonly ComboBox SelectButtonBox = new() { Width = 220 };
	internal readonly ComboBox TransformButtonBox = new() { Width = 220 };
	internal readonly ComboBox PanButtonBox = new() { Width = 220 };
	internal readonly TextBlock MouseError = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
	internal readonly ComboBox AutosaveBox = new() { Width = 220 };
	internal readonly ComboBox BackupBox = new() { Width = 220 };
	internal readonly ComboBox ThemeBox = new() { Width = 220 };
	internal readonly ComboBox SizeBox = new() { Width = 220 };
	private TabItem _mouseTab = null!;

	public SettingsWindow(KeyMap keys, Func<string, bool> shown, PointerButton select, PointerButton transform, PointerButton pan,
						  int autosaveMinutes, int backupCount, string? theme)
	{
		Keys = keys.Copy();
		_shown = shown;
		_themeBefore = ThemeName = ThemeNames.Any(t => t.Name == theme) ? theme! : "Dark";
		Title = "Settings";
		Width = 760;
		Height = 600;
		MinWidth = 560;
		MinHeight = 420;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		foreach (var box in new[] { SelectButtonBox, TransformButtonBox, PanButtonBox })
			box.ItemsSource = Buttons;
		Content = BuildLayout();
		ShowButtons(select, transform, pan);
		ShowSaving(autosaveMinutes, backupCount);
		ShowAppearance();
		ShowList();
		ShowCommand();

		Closed += (_, _) =>
		{
			if (_ok) return;
			ApplyTheme(_themeBefore);   // Cancel: back to how it looked
			UiSize.Set(_sizeBefore);
		};
		KeyDown += (_, e) =>
		{
			if (e.Key == Key.Escape && !ReferenceEquals(FocusManager?.GetFocusedElement(), CaptureBox)) { Close(false); e.Handled = true; }
		};
		UiSize.Fit(this);
	}

	private Control BuildLayout()
	{
		static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
		static Control Row(string label, Control content)
		{
			var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
			row.Children.Add(new TextBlock { Text = label, Width = 200, VerticalAlignment = VerticalAlignment.Center });
			row.Children.Add(content);
			return row;
		}

		// Keys
		var keys = new DockPanel { Margin = new Thickness(10) };
		DockPanel.SetDock(FilterBox, Dock.Top);
		FilterBox.Margin = new Thickness(0, 0, 0, 6);
		FilterBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) ShowList(); };   // (as it's typed)
		keys.Children.Add(FilterBox);
		var details = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
		details.Children.Add(CommandTitle);
		details.Children.Add(CommandKeys);
		CaptureBox.AddHandler(KeyDownEvent, Capture, RoutingStrategies.Tunnel);
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
		buttons.Children.Add(CaptureBox);
		foreach (var b in new[] { SetButton, AddButton, ClearButton, DefaultButton }) buttons.Children.Add(b);
		details.Children.Add(buttons);
		details.Children.Add(ConflictText);
		details.Children.Add(Note("Set: the key pressed becomes this command's only key. Add: one more key for it. A key another command had is taken from it."));
		DockPanel.SetDock(details, Dock.Bottom);
		keys.Children.Add(details);
		keys.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = CommandList });
		CommandList.SelectionChanged += (_, _) => ShowCommand();
		SetButton.Click += (_, _) => SetKeys(add: false);
		AddButton.Click += (_, _) => SetKeys(add: true);
		ClearButton.Click += (_, _) => { if (Selected is { } c) { Keys.Set(c.Id, Array.Empty<KeyChord>()); RefreshRows(); } };
		DefaultButton.Click += (_, _) => { if (Selected is { } c) { Keys.Reset(c.Id); RefreshRows(); } };

		// Mouse
		var mouse = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
		mouse.Children.Add(Row("Pick (and box-select)", SelectButtonBox));
		mouse.Children.Add(Row("Move and turn things", TransformButtonBox));
		mouse.Children.Add(Row("Move the view", PanButtonBox));
		mouse.Children.Add(MouseError);
		mouse.Children.Add(Note("The wheel zooms, whichever buttons these are. In the paint editor, the left button paints and the right one paints with the second colour."));

		// Saving
		var saving = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
		saving.Children.Add(Row("Keep a recovery copy", AutosaveBox));
		saving.Children.Add(Note("Unsaved work is copied aside this often. If Harlequin closes without saving (a crash, a power cut), the next start offers it back."));
		saving.Children.Add(Row("Backups when saving over a project", BackupBox));
		saving.Children.Add(Note("The versions a save replaces, kept in a Backups folder next to the project."));
		var recovery = new Button { Content = "Open the recovery folder" };
		recovery.Click += (_, _) => OpenFolder(Recovery.Folder);
		saving.Children.Add(recovery);

		// Appearance
		var look = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
		look.Children.Add(Row("Theme", ThemeBox));
		look.Children.Add(Row("Size of the panels and dialogs", SizeBox));
		look.Children.Add(Note("Both show at once; Cancel puts them back. The viewport and the timeline keep the screen's own size."));
		ThemeBox.SelectionChanged += (_, _) => { if (ThemeBox.SelectedItem is ThemeChoice t) ApplyTheme(t.Name); };
		SizeBox.SelectionChanged += (_, _) => { if (SizeBox.SelectedIndex >= 0) UiSize.Set(UiSize.Choices[SizeBox.SelectedIndex]); };

		_mouseTab = new TabItem { Header = "Mouse", Content = mouse };
		Tabs.Items.Add(new TabItem { Header = "Keys", Content = keys });
		Tabs.Items.Add(_mouseTab);
		Tabs.Items.Add(new TabItem { Header = "Saving", Content = saving });
		Tabs.Items.Add(new TabItem { Header = "Appearance", Content = look });
		foreach (TabItem tab in Tabs.Items) { tab.FontSize = 15; tab.MinHeight = 32; tab.Padding = new Thickness(10, 4); }

		var reset = new Button { Content = "Everything as at first" };
		reset.Click += (_, _) => ResetAll();
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		ok.Click += (_, _) => TryOk();
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		cancel.Click += (_, _) => Close(false);
		var bottom = new DockPanel { Margin = new Thickness(10) };
		DockPanel.SetDock(reset, Dock.Left);
		bottom.Children.Add(reset);
		bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });

		var root = new DockPanel();
		DockPanel.SetDock(bottom, Dock.Bottom);
		root.Children.Add(bottom);
		root.Children.Add(Tabs);
		return root;
	}

	// ======================= keys =======================

	private KeyCommand? Selected => (CommandList.SelectedItem as ListBoxItem)?.Tag as KeyCommand;

	private bool Matches(KeyCommand command)
	{
		string filter = FilterBox.Text?.Trim() ?? "";
		return filter.Length == 0 ||
			   command.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
			   command.Group.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
			   Keys.Describe(command.Id).Contains(filter, StringComparison.OrdinalIgnoreCase);
	}

	// The commands (the ones the filter matches), with their keys
	private void ShowList()
	{
		var picked = Selected;
		var items = new List<ListBoxItem>();
		foreach (var command in KeyMap.Commands.Where(c => _shown(c.Id) && Matches(c)))
		{
			var keys = new TextBlock { Text = Keys.Describe(command.Id), Width = 170, Foreground = Brushes.Gray };
			var name = new TextBlock { Text = $"{command.Group}: {command.Name}", TextTrimming = TextTrimming.CharacterEllipsis };
			var row = new DockPanel();
			DockPanel.SetDock(keys, Dock.Right);
			row.Children.Add(keys);
			row.Children.Add(name);
			items.Add(new ListBoxItem { Content = row, Tag = command, Padding = new Thickness(6, 2) });
		}
		CommandList.ItemsSource = items;
		CommandList.SelectedItem = items.FirstOrDefault(i => i.Tag == picked);
	}

	internal void Pick(string id) =>
		CommandList.SelectedItem = CommandList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as KeyCommand)?.Id == id);

	// Every row: a change can take a key from another command
	private void RefreshRows()
	{
		ShowList();
		ShowCommand();
	}

	private void ShowCommand()
	{
		bool any = Selected != null;
		SetButton.IsEnabled = AddButton.IsEnabled = any && _captured != null;
		ClearButton.IsEnabled = DefaultButton.IsEnabled = any;
		if (Selected is not { } command)
		{
			CommandTitle.Text = "Pick a command above";
			CommandKeys.Text = "";
			ConflictText.Text = "";
			return;
		}
		CommandTitle.Text = $"{command.Group}: {command.Name}";
		string keys = Keys.Describe(command.Id);
		string defaults = string.Join(", ", command.Defaults);
		CommandKeys.Text = (keys.Length > 0 ? $"Keys: {keys}" : "No key") +
			(Keys.IsDefault(command.Id) ? "" : defaults.Length > 0 ? $"   (at first: {defaults})" : "   (at first: none)") +
			(command.Scope == KeyScope.Paint ? "   · over the paint canvas" : "");
		ShowConflict();
	}

	// Who has the key pressed now
	private void ShowConflict()
	{
		ConflictText.Text = "";
		if (_captured is not { } chord || Selected is not { } command) return;
		if (Keys.KeysOf(command.Id).Contains(chord))
			ConflictText.Text = $"{chord} already does this.";
		else if (Keys.UsedBy(chord, command.Id) is { } other && KeyMap.Find(other) is { } taken)
			ConflictText.Text = $"{chord} is \"{taken.Name}\" ({taken.Group}) now: Set or Add takes it from there.";
	}

	// Every key pressed in the box is one to capture (Tab, Esc and Enter too)
	private void Capture(object? sender, KeyEventArgs e)
	{
		e.Handled = true;
		CaptureKey(e.Key, e.KeyModifiers);
	}

	internal void CaptureKey(Key key, KeyModifiers modifiers)
	{
		if (KeyChord.From(key, modifiers) is not { } chord) return;   // just Ctrl, Shift or Alt so far
		_captured = chord;
		CaptureBox.Text = chord.ToString();
		ShowCommand();
	}

	internal void SetKeys(bool add)
	{
		if (Selected is not { } command || _captured is not { } chord) return;
		Keys.Set(command.Id, add ? Keys.KeysOf(command.Id).Append(chord).ToList() : new[] { chord });
		RefreshRows();
	}

	private void ResetAll()
	{
		Keys.ResetAll();
		var defaults = new AppSettings();
		ShowButtons(defaults.SelectButton, defaults.TransformButton, defaults.PanButton);
		ShowSaving(defaults.AutosaveMinutes, defaults.BackupCount);
		RefreshRows();
	}

	// ======================= mouse =======================

	private void ShowButtons(PointerButton select, PointerButton transform, PointerButton pan)
	{
		SelectButtonBox.SelectedItem = Buttons.FirstOrDefault(b => b.Button == select) ?? Buttons[0];
		TransformButtonBox.SelectedItem = Buttons.FirstOrDefault(b => b.Button == transform) ?? Buttons[1];
		PanButtonBox.SelectedItem = Buttons.FirstOrDefault(b => b.Button == pan) ?? Buttons[2];
		MouseError.Text = "";
	}

	internal void PickButtons(PointerButton select, PointerButton transform, PointerButton pan) => ShowButtons(select, transform, pan);

	private static PointerButton ButtonIn(ComboBox box) => (box.SelectedItem as ButtonChoice)?.Button ?? PointerButton.Left;

	// ======================= saving =======================

	private void ShowSaving(int autosaveMinutes, int backupCount)
	{
		Fill(AutosaveBox, AutosaveMinuteChoices, autosaveMinutes, AutosaveLabel);
		Fill(BackupBox, BackupChoices, backupCount, BackupLabel);
	}

	public static string AutosaveLabel(int minutes) => minutes switch
	{
		<= 0 => "Never",
		1 => "Every minute",
		_ => $"Every {minutes} minutes"
	};

	public static string BackupLabel(int count) => count switch
	{
		<= 0 => "None",
		1 => "The last version",
		_ => $"The last {count} versions"
	};

	// The usual choices, and the one there is now if it isn't one of them (a hand-edited settings file)
	private static void Fill(ComboBox box, int[] usual, int now, Func<int, string> label)
	{
		now = Math.Max(0, now);
		var values = usual.Contains(now) ? usual : usual.Append(now).Order().ToArray();
		var choices = values.Select(v => new Choice(v, label(v))).ToList();
		box.ItemsSource = choices;
		box.SelectedItem = choices.First(c => c.Value == now);
	}

	internal void PickSaving(int minutes, int backups)
	{
		AutosaveBox.SelectedItem = AutosaveBox.Items.OfType<Choice>().FirstOrDefault(c => c.Value == minutes);
		BackupBox.SelectedItem = BackupBox.Items.OfType<Choice>().FirstOrDefault(c => c.Value == backups);
	}

	private static int ValueIn(ComboBox box) => (box.SelectedItem as Choice)?.Value ?? 0;

	private async void OpenFolder(string folder)
	{
		try
		{
			Directory.CreateDirectory(folder);
			if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
			else await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
		}
		catch (Exception ex)
		{
			await Dialogs.Tell(this, "Harlequin", $"Couldn't open the folder:\n\n{ex.Message}");
		}
	}

	// ======================= appearance =======================

	private void ShowAppearance()
	{
		var themes = ThemeNames.Select(t => new ThemeChoice(t.Name, t.Variant)).ToList();
		ThemeBox.ItemsSource = themes;
		ThemeBox.SelectedItem = themes.First(t => t.Name == ThemeName);
		SizeBox.ItemsSource = UiSize.Choices.Select(UiSize.Name).ToList();
		int at = Array.FindIndex(UiSize.Choices, c => Math.Abs(c - UiSize.Current) < 0.01);
		SizeBox.SelectedIndex = at >= 0 ? at : Array.IndexOf(UiSize.Choices, 1.0);
	}

	internal void PickTheme(string name) => ThemeBox.SelectedItem = ThemeBox.Items.OfType<ThemeChoice>().FirstOrDefault(t => t.Name == name);

	internal void PickSize(double size) => SizeBox.SelectedIndex = Array.FindIndex(UiSize.Choices, c => Math.Abs(c - size) < 0.01);

	// The whole application in this theme (null or unknown: dark)
	public static void ApplyTheme(string? name)
	{
		if (Application.Current is { } app)
			app.RequestedThemeVariant = ThemeNames.FirstOrDefault(t => t.Name == name).Variant ?? ThemeVariant.Dark;
	}

	// ======================= OK =======================

	internal void TryOk()
	{
		var select = ButtonIn(SelectButtonBox);
		var transform = ButtonIn(TransformButtonBox);
		var pan = ButtonIn(PanButtonBox);
		if (select == transform || select == pan || transform == pan)
		{
			MouseError.Text = "Picking, moving and the view each need a mouse button of their own.";
			Tabs.SelectedItem = _mouseTab;
			return;
		}
		SelectButton = select;
		TransformButton = transform;
		PanButton = pan;
		AutosaveMinutes = ValueIn(AutosaveBox);
		BackupCount = ValueIn(BackupBox);
		ThemeName = (ThemeBox.SelectedItem as ThemeChoice)?.Name ?? "Dark";
		Scale = UiSize.Current;
		_ok = true;
		if (IsVisible) Close(true);   // (the tests ask it without showing it)
	}
}
