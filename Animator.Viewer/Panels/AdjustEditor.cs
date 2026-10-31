using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Animator.Viewer.Panels;

// A colour layer's settings as sliders and switches, built into a panel (the rig panel's Colour
// view, and the paint editor's layers). Changes go out through the callbacks: new numbers (with
// what changed, so a slider drag can be one undo step), and the switches, which aren't keyed.
public sealed class AdjustEditor
{
	private readonly ColorAdjust _adjust;
	private AdjustValues _values;
	private readonly Action<AdjustValues, string> _setValues;
	private readonly Action<string, Action<ColorAdjust>> _setSwitch;
	private readonly Func<Vector3, string, Vector3?> _pickColor;
	private bool _building = true;

	public static Brush LabelBrush => Themes.Brush("Text");        // (the theme's, when made)
	public static Brush FaintBrush => Themes.Brush("TextMuted");

	private static Brush Freeze(Brush brush)
	{
		brush.Freeze();
		return brush;
	}

	private AdjustEditor(ColorAdjust adjust, AdjustValues values, Action<AdjustValues, string> setValues,
		Action<string, Action<ColorAdjust>> setSwitch, Func<Vector3, string, Vector3?> pickColor)
	{
		_adjust = adjust;
		_values = values;
		_setValues = setValues;
		_setSwitch = setSwitch;
		_pickColor = pickColor;
	}

	// values: the numbers to show (its rest ones, or as the animation has them here)
	public static void Build(Panel host, ColorAdjust adjust, AdjustValues values, Action<AdjustValues, string> setValues,
		Action<string, Action<ColorAdjust>> setSwitch, Func<Vector3, string, Vector3?> pickColor)
	{
		host.Children.Clear();
		var editor = new AdjustEditor(adjust, values, setValues, setSwitch, pickColor);
		editor.Fill(host);
		editor._building = false;
	}

	private void Fill(Panel host)
	{
		var v = _values;
		Percent(host, "Strength", v.Mix, 0, 1, x => _values with { Mix = x },
			"How strongly it changes the colours (keyed, it can fade in and out)");
		switch (_adjust.Kind)
		{
			case ColorAdjustKind.HueSaturation:
				SliderRow(host, "Hue", v.Hue, -180, 180, "0°", x => _values with { Hue = x },
					_adjust.Colorize ? "The one hue everything becomes" : "Round the colour wheel (180° = the opposite colours)");
				Percent(host, "Saturation", v.Saturation, -1, 1, x => _values with { Saturation = x },
					_adjust.Colorize ? "How rich that hue is" : "Greyer (to -100%) or richer");
				Percent(host, "Lightness", v.Lightness, -1, 1, x => _values with { Lightness = x }, "Towards black or white");
				Switch(host, "Colorize (all one hue)", _adjust.Colorize, on => a => a.Colorize = on,
					"Every colour becomes the hue above, keeping its lightness");
				break;
			case ColorAdjustKind.BrightnessContrast:
				Percent(host, "Brightness", v.Brightness, -1, 1, x => _values with { Brightness = x }, null);
				Percent(host, "Contrast", v.Contrast, -1, 1, x => _values with { Contrast = x }, "Flatter (to -100%: all grey) or punchier");
				break;
			case ColorAdjustKind.Tint:
				ColorRow(host, "Colour", v.TintColor, c => _values with { TintColor = c });
				Percent(host, "Amount", v.TintAmount, 0, 1, x => _values with { TintAmount = x }, null);
				Switch(host, "Keep the light (a coloured filter)", _adjust.KeepLight, on => a => a.KeepLight = on,
					"On: like looking through coloured glass, lights stay light. Off: everything goes towards the colour (a flash)");
				break;
			case ColorAdjustKind.ColorBalance:
				Balance(host, "Shadows", () => _values.Shadows, c => _values with { Shadows = c });
				Balance(host, "Midtones", () => _values.Midtones, c => _values with { Midtones = c });
				Balance(host, "Highlights", () => _values.Highlights, c => _values with { Highlights = c });
				break;
			case ColorAdjustKind.ReplaceColor:
				ColorRow(host, "This colour", v.From, c => _values with { From = c });
				ColorRow(host, "Becomes", v.To, c => _values with { To = c });
				Percent(host, "Tolerance", v.Tolerance, 0, 1, x => _values with { Tolerance = x }, "How different a colour may be and still count");
				Percent(host, "Soft edge", v.Softness, 0, 1, x => _values with { Softness = x }, "Colours a little further off change partly");
				break;
		}
	}

	private void Changed(AdjustValues values, string what)
	{
		if (_building) return;
		_values = values;
		_setValues(values, what);
	}

	// A row: its name, the slider, the number
	private void SliderRow(Panel host, string name, float value, float min, float max, string format,
		Func<float, AdjustValues> with, string? tip, float shown = 1f)
	{
		var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), ToolTip = tip };
		row.Children.Add(Label(name, 74));
		var number = new TextBlock { Width = 40, Foreground = FaintBrush, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
		DockPanel.SetDock(number, Dock.Right);
		row.Children.Add(number);
		var slider = new Slider { Minimum = min * shown, Maximum = max * shown, Value = value * shown, VerticalAlignment = VerticalAlignment.Center };
		void Show() => number.Text = slider.Value.ToString(format);
		Show();
		slider.ValueChanged += (_, e) =>
		{
			Show();
			Changed(with((float)e.NewValue / shown), name);
		};
		row.Children.Add(slider);
		host.Children.Add(row);
	}

	private void Percent(Panel host, string name, float value, float min, float max, Func<float, AdjustValues> with, string? tip) =>
		SliderRow(host, name, value, min, max, "0'%'", with, tip, shown: 100f);

	private static TextBlock Label(string text, double width) => new()
	{
		Text = text,
		Width = width,
		Foreground = LabelBrush,
		VerticalAlignment = VerticalAlignment.Center,
		TextTrimming = TextTrimming.CharacterEllipsis
	};

	private void Switch(Panel host, string name, bool on, Func<bool, Action<ColorAdjust>> change, string tip)
	{
		var box = new CheckBox { Content = name, IsChecked = on, Foreground = LabelBrush, Margin = new Thickness(0, 3, 0, 3), ToolTip = tip };
		box.Click += (_, _) => _setSwitch(name, change(box.IsChecked == true));
		host.Children.Add(box);
	}

	private void ColorRow(Panel host, string name, Vector3 color, Func<Vector3, AdjustValues> with)
	{
		var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
		row.Children.Add(Label(name, 74));
		var swatch = new Border { Width = 40, Height = 14, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Background = BrushOf(color) };
		var button = new Button { Content = swatch, Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Pick the colour" };
		button.Click += (_, _) =>
		{
			if (_pickColor(color, name) is not { } picked) return;
			color = picked;
			swatch.Background = BrushOf(picked);
			Changed(with(picked), name);
		};
		row.Children.Add(button);
		host.Children.Add(row);
	}

	// Three sliders for one range: each pushes red, green or blue (away from its opposite)
	private void Balance(Panel host, string name, Func<Vector3> now, Func<Vector3, AdjustValues> with)
	{
		host.Children.Add(new TextBlock { Text = name, Foreground = FaintBrush, Margin = new Thickness(0, 4, 0, 0) });
		var push = now();
		Percent(host, "Cyan ↔ Red", push.X, -1, 1, x => with(now() with { X = x }), "Towards cyan (−) or red (+)");
		Percent(host, "Mag. ↔ Green", push.Y, -1, 1, x => with(now() with { Y = x }), "Towards magenta (−) or green (+)");
		Percent(host, "Yellow ↔ Blue", push.Z, -1, 1, x => with(now() with { Z = x }), "Towards yellow (−) or blue (+)");
	}

	public static Brush BrushOf(Vector3 c)
	{
		var brush = new SolidColorBrush(Color.FromRgb(Byte(c.X), Byte(c.Y), Byte(c.Z)));
		brush.Freeze();
		return brush;
	}

	private static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
}
