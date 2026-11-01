using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Animator.Viewer.Panels;

// Dragging a number field left or right changes its number (as in most animation programs):
// a step a pixel, Shift ten times faster, Ctrl ten times finer. A click without dragging types
// in it as before. While dragging, the field commits each value the way leaving it would (its
// LostFocus handler), so the viewport follows along; begin and end wrap the drag (one undo step).
public sealed class NumberScrub
{
	private readonly TextBox _box;
	private readonly double _step, _min, _max;
	private readonly bool _whole;
	private readonly Action _begin, _end;

	private bool _pressed, _scrubbing;
	private double _lastX, _value;

	public static NumberScrub Attach(TextBox box, double step, Action begin, Action end,
		double min = double.NegativeInfinity, double max = double.PositiveInfinity, bool whole = false) =>
		new(box, step, begin, end, min, max, whole);

	private NumberScrub(TextBox box, double step, Action begin, Action end, double min, double max, bool whole)
	{
		_box = box;
		_step = step;
		_begin = begin;
		_end = end;
		_min = min;
		_max = max;
		_whole = whole;
		box.Cursor = Cursors.SizeWE;
		box.PreviewMouseLeftButtonDown += Down;
		box.PreviewMouseMove += Move;
		box.PreviewMouseLeftButtonUp += Up;
		box.LostMouseCapture += (_, _) => Finish();
		box.GotKeyboardFocus += (_, _) => box.Cursor = Cursors.IBeam;
		box.LostKeyboardFocus += (_, _) => box.Cursor = Cursors.SizeWE;
		string tip = "Drag left or right to change it (Shift: faster, Ctrl: finer), or click to type";
		box.ToolTip = box.ToolTip is string old && old.Length > 0 ? $"{old}\n{tip}" : tip;
	}

	public bool Scrubbing => _scrubbing;

	private void Down(object sender, MouseButtonEventArgs e)
	{
		if (_box.IsKeyboardFocusWithin || !_box.IsEnabled || _box.IsReadOnly) return;   // typing in it: the mouse is the text's
		if (!TryRead(_box.Text, out _value)) return;
		_pressed = true;
		_lastX = e.GetPosition(_box).X;
		_box.CaptureMouse();
		e.Handled = true;
	}

	private void Move(object sender, MouseEventArgs e)
	{
		if (!_pressed) return;
		double x = e.GetPosition(_box).X;
		if (!_scrubbing)
		{
			if (Math.Abs(x - _lastX) < 3) return;
			_scrubbing = true;
			_begin();
		}
		var mods = Keyboard.Modifiers;
		double factor = mods.HasFlag(ModifierKeys.Shift) ? 10 : mods.HasFlag(ModifierKeys.Control) && !_whole ? 0.1 : 1;
		_value = Math.Clamp(_value + (x - _lastX) * _step * factor, _min, _max);
		_lastX = x;

		double shown = _whole ? Math.Round(_value) : Math.Round(_value / (_step * factor)) * _step * factor;
		string text = Format(shown);
		if (text == _box.Text) return;
		_box.Text = text;
		_box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, _box));   // commit it, as leaving the field does
	}

	private void Up(object sender, MouseButtonEventArgs e)
	{
		if (!_pressed) return;
		bool scrubbed = _scrubbing;
		_box.ReleaseMouseCapture();   // (→ Finish)
		Finish();
		e.Handled = true;
		if (!scrubbed)
		{
			// Just a click: type in it
			_box.Focus();
			_box.SelectAll();
		}
	}

	private void Finish()
	{
		_pressed = false;
		if (!_scrubbing) return;
		_scrubbing = false;
		_end();
	}

	private string Format(double v) => _whole
		? ((long)Math.Round(v)).ToString(CultureInfo.CurrentCulture)
		: Math.Round(v, 3).ToString("0.###", CultureInfo.CurrentCulture);

	private static bool TryRead(string text, out double value) =>
		double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
		double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
