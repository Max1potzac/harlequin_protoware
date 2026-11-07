using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Animator.Desktop;

// A number in a panel: a label, and a box to type it in (Enter or leaving the box sets it). The
// label can be dragged left and right to change the number by Step a screen unit (Shift: ten
// times as fast, Ctrl: a tenth), the whole drag one undo step (Begin, End: the window groups it).
// Read: what it shows now (null: nothing, and the box is off). Commit: a number set.
public sealed class NumberField : Grid
{
	private readonly TextBlock _label;
	internal readonly TextBox Box;
	private bool _syncing;
	private Point? _dragFrom;
	private float _dragStart;

	public float Step { get; set; } = 1f;
	public float Min { get; set; } = float.NegativeInfinity;
	public float Max { get; set; } = float.PositiveInfinity;
	public bool Whole { get; set; }
	public Func<float?> Read { get; set; } = () => null;
	public Action<float> Commit { get; set; } = _ => { };
	public Action? Begin { get; set; }
	public Action? End { get; set; }
	public Action<string>? Problem { get; set; }   // a typed text that isn't a number

	public NumberField(string label, float step = 1f)
	{
		Step = step;
		ColumnDefinitions = new ColumnDefinitions("78,*");
		Margin = new Thickness(0, 2);
		_label = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.SizeWestEast) };
		Box = new TextBox { MinHeight = 26, Padding = new Thickness(6, 2) };
		SetColumn(_label, 0);
		SetColumn(Box, 1);
		Children.Add(_label);
		Children.Add(Box);

		Box.KeyDown += (_, e) =>
		{
			if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
			else if (e.Key == Key.Escape) { Refresh(force: true); e.Handled = true; }
		};
		Box.LostFocus += (_, _) => CommitText();

		_label.PointerPressed += (_, e) =>
		{
			if (Read() is not { } v || !e.GetCurrentPoint(_label).Properties.IsLeftButtonPressed) return;
			_dragFrom = e.GetPosition(this);
			_dragStart = v;
			e.Pointer.Capture(_label);
			Begin?.Invoke();
			e.Handled = true;
		};
		_label.PointerMoved += (_, e) =>
		{
			if (_dragFrom is not { } from) return;
			double dx = e.GetPosition(this).X - from.X;
			float speed = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10f : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.1f : 1f;
			Set(_dragStart + (float)dx * Step * speed);
		};
		_label.PointerReleased += (_, e) =>
		{
			if (_dragFrom == null) return;
			_dragFrom = null;
			e.Pointer.Capture(null);
			End?.Invoke();
		};
	}

	public bool Dragging => _dragFrom != null;

	private void Set(float v)
	{
		v = Math.Clamp(v, Min, Max);
		if (Whole) v = MathF.Round(v);
		Commit(v);
		Refresh(force: true);
	}

	// (as if typed, and Enter pressed)
	internal void Type(string text)
	{
		Box.Text = text;
		CommitText();
	}

	internal TextBlock Label => _label;

	private void CommitText()
	{
		if (_syncing || Read() is not { } current) return;
		string text = Box.Text?.Trim() ?? "";
		if (text == EditorSession.Num(current)) return;
		if (!EditorSession.TryNum(text, out float v))
		{
			Problem?.Invoke($"\"{text}\" isn't a number.");
			Refresh(force: true);
			return;
		}
		Set(v);
	}

	// What it shows made current (not while it's being typed in, unless forced)
	public void Refresh(bool force = false)
	{
		if (!force && Box.IsFocused) return;
		var v = Read();
		string text = EditorSession.Num(v);
		IsEnabled = v != null;
		if (Box.Text == text) return;
		_syncing = true;
		Box.Text = text;
		_syncing = false;
	}
}
