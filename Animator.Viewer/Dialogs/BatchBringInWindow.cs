using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Animator.Viewer.Dialogs;

// Edit → Bring in from Animate projects…: many characters' heads (or any other part of them) as
// skins here, in one go, without adding them to the project as objects. Each file is imported
// aside, matched to this object by name (as Bring in from another object… does), and the parts
// on the picked bones (and the accessories that ride on them) become a skin named after the file.
// Built in code (no XAML).
public sealed class BatchBringInWindow : Window
{
	private readonly ListBox _files = new() { Height = 200, SelectionMode = SelectionMode.Extended };
	private readonly ComboBox _category = new() { Width = 170, IsEditable = true };
	private readonly CheckBox _animations = new() { Content = "Their animations too (named …_egypt, …_pirate and so on)", Margin = new Thickness(0, 6, 0, 0) };
	private readonly CheckBox _asPlaced = new() { Content = "Line them up as they show in their own animations (right for characters that share animations)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
	private readonly CheckBox _accessories = new() { Content = "Bring the parts this object hasn't too (hats, hair, a scarf), shown only with their skin", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
	private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

	public IReadOnlyList<string> Files => _files.Items.Cast<string>().ToList();
	public string Category => _category.Text?.Trim() ?? "";
	public bool Animations => _animations.IsChecked == true;
	public bool AsPlaced => _asPlaced.IsChecked == true;
	public bool Accessories => _accessories.IsChecked == true;

	public BatchBringInWindow(string targetName, IReadOnlyCollection<Bone> picked, IEnumerable<string> categories, string? folder)
	{
		Title = $"Bring in from Animate projects, into {targetName}";
		Width = 640;
		SizeToContent = SizeToContent.Height;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		SetResourceReference(BackgroundProperty, Themes.Key("Window"));
		SetResourceReference(ForegroundProperty, Themes.Key("Text"));

		var panel = new StackPanel { Margin = new Thickness(14) };
		var note = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Text = "Each project is imported aside (not added to this project). Its parts are matched to this object's by name, " +
				   "and the ones on the picked bones become a skin named after the file: a zombie's head, from the head bone. " +
				   (picked.Count == 0
					   ? "No bones are picked in this object: close this, pick the head bone in the rig panel, and come back."
					   : $"Picked: {string.Join(", ", picked.Select(b => b.Name))} (and the bones under them).")
		};
		panel.Children.Add(note);

		panel.Children.Add(new TextBlock { Text = "Projects", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) });
		panel.Children.Add(_files);
		var fileButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
		var add = new Button { Content = "Add…", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(0, 0, 6, 0) };
		add.Click += (_, _) =>
		{
			var open = new OpenFileDialog
			{
				Title = "Animate projects (.fla, .xfl, the DOMDocument.xml in an XFL folder, a zipped XFL folder)",
				Filter = "Animate projects|*.fla;*.xfl;DOMDocument.xml;*.zip",
				Multiselect = true
			};
			if (folder != null && Directory.Exists(folder)) open.InitialDirectory = folder;
			if (open.ShowDialog(this) != true) return;
			foreach (string f in open.FileNames)
				if (!_files.Items.Contains(f)) _files.Items.Add(f);
		};
		var remove = new Button { Content = "Remove", Padding = new Thickness(10, 1, 10, 1) };
		remove.Click += (_, _) =>
		{
			foreach (var item in _files.SelectedItems.Cast<object>().ToList()) _files.Items.Remove(item);
		};
		fileButtons.Children.Add(add);
		fileButtons.Children.Add(remove);
		panel.Children.Add(fileButtons);

		panel.Children.Add(new TextBlock { Text = "Skins", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) });
		var category = new StackPanel { Orientation = Orientation.Horizontal };
		category.Children.Add(new TextBlock { Text = "Category", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
		_category.ItemsSource = categories.Append("Head").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		_category.Text = "Head";
		category.Children.Add(_category);
		panel.Children.Add(category);
		panel.Children.Add(_accessories);
		panel.Children.Add(_asPlaced);
		panel.Children.Add(_animations);
		panel.Children.Add(_problem);

		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
		var ok = new Button { Content = "Bring them in", IsDefault = true, Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0, 0, 6, 0) };
		ok.Click += (_, _) =>
		{
			if (_files.Items.Count == 0) _problem.Text = "Add the projects to bring in first.";
			else if (picked.Count == 0) _problem.Text = "Pick the bones whose parts come in first (the head bone, for heads): close this and pick it in the rig panel.";
			else DialogResult = true;
		};
		var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 2, 14, 2) };
		buttons.Children.Add(ok);
		buttons.Children.Add(cancel);
		panel.Children.Add(buttons);
		Content = panel;
	}
}
