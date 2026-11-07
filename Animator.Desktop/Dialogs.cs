using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Animator.Desktop;

// Small questions and messages (Avalonia has no message box of its own)
public static class Dialogs
{
	// The text, and a button for each answer; the one clicked (null: the window was closed)
	public static async Task<string?> Ask(Window owner, string title, string text, params string[] answers)
	{
		var dialog = new Window
		{
			Title = title,
			SizeToContent = SizeToContent.WidthAndHeight,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			CanResize = false,
			ShowInTaskbar = false,
			MaxWidth = 560
		};
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
		foreach (string answer in answers)
		{
			var button = new Button { Content = answer, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
			button.Click += (_, _) => dialog.Close(answer);
			buttons.Children.Add(button);
		}
		dialog.Content = new StackPanel
		{
			Margin = new Thickness(20),
			Spacing = 16,
			Children =
			{
				new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
				buttons
			}
		};
		return await dialog.ShowDialog<string?>(owner);
	}

	public static Task Tell(Window owner, string title, string text) => Ask(owner, title, text, "OK");

	// A line of text asked for, as it starts; problem says what's wrong with one (null: it'll do).
	// Null: cancelled.
	public static async Task<string?> AskText(Window owner, string title, string text, string start, Func<string, string?> problem)
	{
		var dialog = new Window
		{
			Title = title,
			SizeToContent = SizeToContent.WidthAndHeight,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			CanResize = false,
			ShowInTaskbar = false,
			MinWidth = 380,
			MaxWidth = 560
		};
		var box = new TextBox { Text = start };
		var why = new TextBlock { Foreground = Avalonia.Media.Brushes.Orange, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
		var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
		var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
		void Done()
		{
			string answer = box.Text?.Trim() ?? "";
			if (problem(answer) is { } wrong) { why.Text = wrong; return; }
			dialog.Close(answer);
		}
		ok.Click += (_, _) => Done();
		cancel.Click += (_, _) => dialog.Close(null);
		dialog.Content = new StackPanel
		{
			Margin = new Thickness(20),
			Spacing = 10,
			Children =
			{
				new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
				box,
				why,
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
			}
		};
		dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
		return await dialog.ShowDialog<string?>(owner);
	}
}
