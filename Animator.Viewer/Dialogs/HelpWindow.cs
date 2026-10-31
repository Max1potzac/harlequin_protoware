using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Animator.Viewer.Dialogs;

// Help → Keyboard and mouse (F1): what the clicks, drags and keys do where you are (the scene, or
// inside an object), with the keys as they're set now. One window, left open beside the work;
// it follows along as you go in and out of objects. Built in code (no XAML).
public sealed class HelpWindow : Window
{
	private readonly StackPanel _lines = new() { Margin = new Thickness(16, 12, 16, 16) };
	private string _shown = "";

	public HelpWindow()
	{
		Title = "Keyboard and mouse";
		Width = 760;
		Height = 640;
		MinWidth = 360;
		MinHeight = 240;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		SetResourceReference(BackgroundProperty, Themes.Key("Window"));
		SetResourceReference(ForegroundProperty, Themes.Key("Text"));
		Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _lines };
	}

	// The help, a line a topic ("Viewport: …", "Timeline: …"); within a line, three spaces part its points
	public void ShowText(string text)
	{
		if (text == _shown) return;
		_shown = text;
		_lines.Children.Clear();
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
					block.Inlines.Add(new Run(point[..(colon + 1)]) { FontWeight = FontWeights.Bold });
					block.Inlines.Add(new Run(point[(colon + 1)..]));
				}
				else block.Inlines.Add(new Run(point));
				_lines.Children.Add(block);
			}
		}
	}
}
