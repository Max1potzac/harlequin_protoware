using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// A colour picked: red, green, blue and opacity sliders, the colour as #AARRGGBB, a swatch.
// Null: cancelled.
public static class ColourDialog
{
	public static async Task<SKColor?> Ask(Window owner, string title, SKColor start)
	{
		var dialog = new Window
		{
			Title = title,
			SizeToContent = SizeToContent.WidthAndHeight,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			CanResize = false,
			ShowInTaskbar = false
		};
		var colour = start;
		bool syncing = false;
		var swatch = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
		var hex = new TextBox { Width = 120 };
		var sliders = new List<(Slider Slider, TextBlock Value)>();
		var rows = new StackPanel { Spacing = 4 };
		foreach (string name in new[] { "Red", "Green", "Blue", "Opacity" })
		{
			var slider = new Slider { Minimum = 0, Maximum = 255, Width = 220, SmallChange = 1, LargeChange = 16 };
			var value = new TextBlock { Width = 36, VerticalAlignment = VerticalAlignment.Center };
			sliders.Add((slider, value));
			var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
			row.Children.Add(new TextBlock { Text = name, Width = 60, VerticalAlignment = VerticalAlignment.Center });
			row.Children.Add(slider);
			row.Children.Add(value);
			rows.Children.Add(row);
		}

		void Show()
		{
			syncing = true;
			byte[] parts = { colour.Red, colour.Green, colour.Blue, colour.Alpha };
			for (int i = 0; i < 4; i++)
			{
				sliders[i].Slider.Value = parts[i];
				sliders[i].Value.Text = parts[i].ToString();
			}
			swatch.Background = new SolidColorBrush(Color.FromArgb(colour.Alpha, colour.Red, colour.Green, colour.Blue));
			if (!hex.IsFocused) hex.Text = Hex(colour);
			syncing = false;
		}
		foreach (var (slider, _) in sliders)
			slider.PropertyChanged += (_, e) =>
			{
				if (syncing || e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
				byte B(int i) => (byte)Math.Round(sliders[i].Slider.Value);
				colour = new SKColor(B(0), B(1), B(2), B(3));
				Show();
			};
		hex.LostFocus += (_, _) => { if (SKColor.TryParse(hex.Text?.Trim() ?? "", out var c)) colour = c; Show(); };
		hex.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter && SKColor.TryParse(hex.Text?.Trim() ?? "", out var c)) { colour = c; Show(); } };

		var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
		var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
		ok.Click += (_, _) => { if (SKColor.TryParse(hex.Text?.Trim() ?? "", out var c)) colour = c; dialog.Close(true); };
		cancel.Click += (_, _) => dialog.Close(false);
		var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
		top.Children.Add(swatch);
		top.Children.Add(rows);
		dialog.Content = new StackPanel
		{
			Margin = new Thickness(20),
			Spacing = 12,
			Children =
			{
				top,
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center }, hex } },
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
			}
		};
		Show();
		bool done = await dialog.ShowDialog<bool>(owner);
		return done ? colour : null;
	}

	public static string Hex(SKColor c) => $"#{c.Alpha:X2}{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

	public static Color ToAvalonia(SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
}
