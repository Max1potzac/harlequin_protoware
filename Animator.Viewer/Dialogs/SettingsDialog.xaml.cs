using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Animator.Viewer.Dialogs;

// One command in the list
public sealed class KeyRow : INotifyPropertyChanged
{
	public KeyRow(KeyCommand command, string keys)
	{
		Command = command;
		_keys = keys;
	}

	public KeyCommand Command { get; }
	public string Group => Command.Group;
	public string Name => Command.Name;

	private string _keys;
	public string Keys
	{
		get => _keys;
		set
		{
			if (_keys == value) return;
			_keys = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Keys)));
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;
}

// Settings: which key does what (any command, paint tools included), which mouse button picks,
// which moves things and which moves the view, and how work is kept safe (autosave, backups).
// Nothing changes until OK.
public partial class SettingsDialog : Window
{
	private sealed record ButtonChoice(MouseButton Button, string Label)
	{
		public override string ToString() => Label;
	}

	private static readonly ButtonChoice[] Buttons =
	{
		new(MouseButton.Left, "Left button"),
		new(MouseButton.Right, "Right button"),
		new(MouseButton.Middle, "Middle button (wheel)"),
		new(MouseButton.XButton1, "Side button (back)"),
		new(MouseButton.XButton2, "Side button (forward)")
	};

	// A number to pick, as the list shows it
	private sealed record Choice(int Value, string Label)
	{
		public override string ToString() => Label;
	}

	private static readonly int[] AutosaveMinuteChoices = { 0, 1, 2, 5, 10, 15, 30 };
	private static readonly int[] BackupChoices = { 0, 1, 2, 3, 5, 10, 20 };

	private readonly List<KeyRow> _rows;
	private KeyChord? _captured;

	// What OK leaves: the keys, the buttons, and saving
	public KeyMap Keys { get; }
	public MouseButton SelectButton { get; private set; }
	public MouseButton TransformButton { get; private set; }
	public MouseButton PanButton { get; private set; }
	public int AutosaveMinutes { get; private set; }   // 0: never
	public int BackupCount { get; private set; }       // 0: none
	public string ThemeName { get; private set; } = Themes.Current.Name;

	private readonly Theme _themeBefore = Themes.Current;   // (Cancel: back to it)
	private bool _themeChosen;

	public SettingsDialog(KeyMap keys, MouseButton select, MouseButton transform, MouseButton pan,
						  int autosaveMinutes, int backupCount)
	{
		InitializeComponent();
		Keys = keys.Copy();
		_rows = KeyMap.Commands.Select(c => new KeyRow(c, Keys.Describe(c.Id))).ToList();
		CommandList.ItemsSource = _rows;
		CollectionViewSource.GetDefaultView(_rows).Filter = Matches;

		foreach (var box in new[] { SelectButtonBox, TransformButtonBox, PanButtonBox })
			box.ItemsSource = Buttons;
		ShowButtons(select, transform, pan);
		ShowSaving(autosaveMinutes, backupCount);
		ShowCommand();
		ShowThemes();
		Closed += (_, _) =>
		{
			if (!_themeChosen && Themes.Current != _themeBefore) Themes.Apply(_themeBefore);
		};
		DialogLayout.FitToScreen(this);
	}

	// ======================= appearance =======================

	private bool _showingThemes;

	private void ShowThemes()
	{
		var problems = new List<string>();
		var all = Themes.All(problems);
		_showingThemes = true;
		ThemeBox.ItemsSource = all;
		ThemeBox.SelectedItem = all.FirstOrDefault(t => t.Name == Themes.Current.Name) ?? all[0];
		_showingThemes = false;
		ThemeProblems.Text = problems.Count == 0 ? "" : "Themes that can't be read:\n" + string.Join("\n", problems);
	}

	private void Theme_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_showingThemes || ThemeBox.SelectedItem is not Theme theme) return;
		Themes.Apply(theme);
	}

	private void OpenThemes_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string folder = Themes.PrepareFolder();
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
		{
			ThemeProblems.Text = $"The themes folder can't be opened: {ex.Message}";
		}
	}

	private void ReloadThemes_Click(object sender, RoutedEventArgs e) => ShowThemes();

	private KeyRow? Selected => CommandList.SelectedItem as KeyRow;

	// ======================= the list =======================

	private bool Matches(object item)
	{
		string filter = FilterBox.Text.Trim();
		if (filter.Length == 0 || item is not KeyRow row) return true;
		return row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
			   row.Group.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
			   row.Keys.Contains(filter, StringComparison.OrdinalIgnoreCase);
	}

	private void Filter_Changed(object sender, TextChangedEventArgs e)
	{
		if (IsLoaded) CollectionViewSource.GetDefaultView(_rows).Refresh();
	}

	private void Command_Changed(object sender, SelectionChangedEventArgs e) => ShowCommand();

	// Every row: a change can take a key from another command
	private void RefreshRows()
	{
		foreach (var row in _rows) row.Keys = Keys.Describe(row.Command.Id);
		ShowCommand();
	}

	private void ShowCommand()
	{
		bool any = Selected != null;
		SetButton.IsEnabled = AddButton.IsEnabled = any && _captured != null;
		ClearButton.IsEnabled = DefaultButton.IsEnabled = any;
		if (Selected is not { } row)
		{
			CommandTitle.Text = "Pick a command above";
			CommandKeys.Text = "";
			ConflictText.Text = "";
			return;
		}
		var command = row.Command;
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
		if (_captured is not { } chord || Selected is not { } row) return;
		if (Keys.KeysOf(row.Command.Id).Contains(chord))
			ConflictText.Text = $"{chord} already does this.";
		else if (Keys.UsedBy(chord, row.Command.Id) is { } other && KeyMap.Find(other) is { } command)
			ConflictText.Text = $"{chord} is \"{command.Name}\" ({command.Group}) now: Set or Add takes it from there.";
	}

	// ======================= changing keys =======================

	private void Capture_KeyDown(object sender, KeyEventArgs e)
	{
		e.Handled = true;   // every key is one to capture here (Tab, Esc and Enter too)
		if (KeyChord.From(e) is not { } chord) return;   // just Ctrl, Shift or Alt so far
		_captured = chord;
		CaptureBox.Text = chord.ToString();
		ShowCommand();
	}

	private void Set_Click(object sender, RoutedEventArgs e)
	{
		if (Selected is not { } row || _captured is not { } chord) return;
		Keys.Set(row.Command.Id, new[] { chord });
		RefreshRows();
	}

	private void Add_Click(object sender, RoutedEventArgs e)
	{
		if (Selected is not { } row || _captured is not { } chord) return;
		Keys.Set(row.Command.Id, Keys.KeysOf(row.Command.Id).Append(chord).ToList());
		RefreshRows();
	}

	private void Clear_Click(object sender, RoutedEventArgs e)
	{
		if (Selected is not { } row) return;
		Keys.Set(row.Command.Id, Array.Empty<KeyChord>());
		RefreshRows();
	}

	private void Default_Click(object sender, RoutedEventArgs e)
	{
		if (Selected is not { } row) return;
		Keys.Reset(row.Command.Id);
		RefreshRows();
	}

	private void ResetAll_Click(object sender, RoutedEventArgs e)
	{
		Keys.ResetAll();
		var defaults = new AppSettings();
		ShowButtons(defaults.SelectButton, defaults.TransformButton, defaults.PanButton);
		ShowSaving(defaults.AutosaveMinutes, defaults.BackupCount);
		RefreshRows();
	}

	// ======================= mouse =======================

	private void ShowButtons(MouseButton select, MouseButton transform, MouseButton pan)
	{
		SelectButtonBox.SelectedItem = Buttons.FirstOrDefault(b => b.Button == select) ?? Buttons[0];
		TransformButtonBox.SelectedItem = Buttons.FirstOrDefault(b => b.Button == transform) ?? Buttons[1];
		PanButtonBox.SelectedItem = Buttons.FirstOrDefault(b => b.Button == pan) ?? Buttons[2];
		MouseError.Text = "";
	}

	private static MouseButton ButtonIn(ComboBox box) => (box.SelectedItem as ButtonChoice)?.Button ?? MouseButton.Left;

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		var select = ButtonIn(SelectButtonBox);
		var transform = ButtonIn(TransformButtonBox);
		var pan = ButtonIn(PanButtonBox);
		if (select == transform || select == pan || transform == pan)
		{
			MouseError.Text = "Picking, moving and the view each need a mouse button of their own.";
			MouseTab.IsSelected = true;
			return;
		}
		SelectButton = select;
		TransformButton = transform;
		PanButton = pan;
		AutosaveMinutes = ValueIn(AutosaveBox);
		BackupCount = ValueIn(BackupBox);
		ThemeName = Themes.Current.Name;
		_themeChosen = true;
		DialogResult = true;
	}

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

	private static int ValueIn(ComboBox box) => (box.SelectedItem as Choice)?.Value ?? 0;

	private void OpenRecoveryFolder_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(Recovery.Folder);
			Process.Start(new ProcessStartInfo(Recovery.Folder) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, $"Couldn't open the folder:\n\n{ex.Message}", "Harlequin",
							MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}
}
