using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SkiaSharp;

namespace Animator.Viewer.Dialogs;

// Any colour, with transparency. The square picks saturation (left to right) and
// brightness (top to bottom), the first strip the hue, the second the opacity.
// The fields on the right take exact numbers.
public partial class ColorPickerDialog : Window
{
	// The colour is kept as hue / saturation / value + alpha, so dragging to black or
	// grey and back doesn't lose the hue you were on
	private double _hue;     // 0..360
	private double _sat;     // 0..1
	private double _val;     // 0..1
	private byte _alpha;     // 0..255
	private bool _updating;

	public SKColor Color { get; private set; }

	public ColorPickerDialog(SKColor initial, string title)
	{
		InitializeComponent();
		Title = title;
		Color = initial;
		OldPreview.Fill = new SolidColorBrush(ToWpf(initial));
		SetFromColor(initial);
		Loaded += (_, _) => Refresh();
	}

	// ======================= mouse =======================

	private void Area_MouseDown(object sender, MouseButtonEventArgs e)
	{
		var area = (FrameworkElement)sender;
		area.CaptureMouse();
		PickAt(area, e.GetPosition(area));
	}

	private void Area_MouseMove(object sender, MouseEventArgs e)
	{
		var area = (FrameworkElement)sender;
		if (area.IsMouseCaptured) PickAt(area, e.GetPosition(area));
	}

	private void Area_MouseUp(object sender, MouseButtonEventArgs e) => ((FrameworkElement)sender).ReleaseMouseCapture();

	private void PickAt(FrameworkElement area, Point p)
	{
		double x = Math.Clamp(p.X / area.ActualWidth, 0, 1);
		double y = Math.Clamp(p.Y / area.ActualHeight, 0, 1);

		if (area == SvArea)
		{
			_sat = x;
			_val = 1 - y;
		}
		else if (area == HueArea)
		{
			_hue = y * 360;
		}
		else if (area == AlphaArea)
		{
			_alpha = (byte)Math.Round((1 - y) * 255);
		}
		Refresh();
	}

	// ======================= typed numbers =======================

	private void Field_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		Commit((TextBox)sender);
		e.Handled = true;   // Enter in a field applies it; it doesn't close the dialog
	}

	private void Field_LostFocus(object sender, RoutedEventArgs e) => Commit((TextBox)sender);

	private void Commit(TextBox box)
	{
		if (_updating) return;
		var c = CurrentColor();
		string text = box.Text.Trim();

		switch ((string)box.Tag)
		{
			case "Hex":
				if (!text.StartsWith('#')) text = "#" + text;
				if (SKColor.TryParse(text, out var parsed)) SetFromColor(parsed);
				break;
			case "R" when TryByte(text, out byte r): SetFromColor(new SKColor(r, c.Green, c.Blue, c.Alpha)); break;
			case "G" when TryByte(text, out byte g): SetFromColor(new SKColor(c.Red, g, c.Blue, c.Alpha)); break;
			case "B" when TryByte(text, out byte b): SetFromColor(new SKColor(c.Red, c.Green, b, c.Alpha)); break;
			case "A" when double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out double percent):
				_alpha = (byte)Math.Round(Math.Clamp(percent, 0, 100) * 2.55);
				break;
		}
		Refresh();   // also puts back the old text if what was typed didn't make sense
	}

	private static bool TryByte(string text, out byte value)
	{
		value = 0;
		if (!int.TryParse(text, out int n)) return false;
		value = (byte)Math.Clamp(n, 0, 255);
		return true;
	}

	private void Ok_Click(object sender, RoutedEventArgs e)
	{
		// A field still being typed in counts too
		if (Keyboard.FocusedElement is TextBox box) Commit(box);
		Color = CurrentColor();
		DialogResult = true;
	}

	// ======================= keeping everything in step =======================

	private void SetFromColor(SKColor c)
	{
		var (h, s, v) = ColorMath.ToHsv(c);
		if (s > 0 && v > 0) _hue = h;   // greys and black have no hue: keep the current one
		if (v > 0) _sat = s;
		_val = v;
		_alpha = c.Alpha;
	}

	private SKColor CurrentColor() => ColorMath.FromHsv(_hue, _sat, _val, _alpha);

	private void Refresh()
	{
		_updating = true;
		var color = CurrentColor();
		var wpf = ToWpf(color);
		var opaque = System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue);

		HueFill.Fill = new SolidColorBrush(ToWpf(ColorMath.FromHsv(_hue, 1, 1, 255)));
		AlphaFill.Fill = new LinearGradientBrush(opaque, System.Windows.Media.Color.FromArgb(0, color.Red, color.Green, color.Blue), 90);
		NewPreview.Fill = new SolidColorBrush(wpf);

		Place(SvMarker, _sat * SvArea.ActualWidth - SvMarker.Width / 2, (1 - _val) * SvArea.ActualHeight - SvMarker.Height / 2);
		Place(HueMarker, 0, _hue / 360 * HueArea.ActualHeight - HueMarker.Height / 2);
		Place(AlphaMarker, 0, (1 - _alpha / 255.0) * AlphaArea.ActualHeight - AlphaMarker.Height / 2);
		// The marker stays visible on light and dark colours
		SvMarker.Stroke = _val > 0.6 && _sat < 0.4 ? Brushes.Black : Brushes.White;

		SetText(HexBox, $"#{color.Alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}");
		SetText(RedBox, color.Red.ToString());
		SetText(GreenBox, color.Green.ToString());
		SetText(BlueBox, color.Blue.ToString());
		SetText(AlphaBox, Math.Round(color.Alpha / 2.55).ToString(CultureInfo.CurrentCulture));
		_updating = false;
	}

	private static void Place(Shape marker, double x, double y)
	{
		Canvas.SetLeft(marker, x);
		Canvas.SetTop(marker, y);
	}

	private static void SetText(TextBox box, string text)
	{
		if (!box.IsKeyboardFocused) box.Text = text;
	}

	private static System.Windows.Media.Color ToWpf(SKColor c) =>
		System.Windows.Media.Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
}
