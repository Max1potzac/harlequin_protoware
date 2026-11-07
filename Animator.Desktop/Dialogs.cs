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
}
