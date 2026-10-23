using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Painting;

public enum SelectShape { Rectangle, Ellipse, Polygon, Polyline, Curve, Freehand }

// Selecting by shape: the Figure tool's shapes, making a selection instead of paint.
//  Drag shapes (Rectangle, Ellipse, Polygon, Freehand): press, drag, release.
//    While dragging: Shift = square / circle / 15° steps, Ctrl = from the center.
//  Click shapes (Polyline, Curve): click to add points; double-click or Enter to finish,
//    or click the first point. Backspace removes the last point, Esc cancels.
//  Held when you start: Shift adds to the selection, Alt subtracts, both = intersect.
//  A click without dragging deselects.
public sealed class ShapeSelectTool : PaintTool
{
	public override string Name => "Select";
	public override bool UsesAlt => true;

	private bool _active;
	private SelectShape _shape;           // shape and mode are fixed when you start
	private SelectMode _mode;
	private Vector2 _start;
	private Vector2 _current;
	private readonly List<Vector2> _points = new();

	// Photoshop's rule: Shift held when you start means "add". To also get a square,
	// let go of Shift and press it again while dragging.
	private bool _shiftAtStart;
	private bool _shiftReleased;

	public IReadOnlyList<Vector2> Points => _points;
	public override bool InProgress => PlacingPoints;
	public bool PlacingPoints => _active && ClickShape;

	// The outline of the shape being made, in image pixels (the canvas draws it)
	public SKPath? Preview { get; private set; }

	private bool ClickShape => _shape is SelectShape.Polyline or SelectShape.Curve;

	// ---------- input ----------

	public override void Down(PaintSession s, Vector2 p)
	{
		if (s.Asset == null) return;

		if (!_active)
		{
			_active = true;
			_shape = s.SelectShape;
			_mode = s.ModeForClick();
			_shiftAtStart = s.ShiftHeld;
			_shiftReleased = false;
			_start = _current = p;
			_points.Clear();
			_points.Add(p);
			UpdatePreview(s);
			return;
		}

		// Click shapes: one more point, or done
		bool nearFirst = _points.Count >= 3 && Vector2.Distance(p, _points[0]) <= 8f / s.ViewZoom;
		if (nearFirst || s.ClickCount >= 2)
		{
			Finish(s);
			return;
		}
		_points.Add(p);
		_current = p;
		UpdatePreview(s);
	}

	public override void Move(PaintSession s, Vector2 p)
	{
		if (!_active) return;
		TrackShift(s);
		_current = p;
		if (_shape == SelectShape.Freehand && Vector2.Distance(p, _points[^1]) >= 0.5f) _points.Add(p);
		else if (ClickShape) _points[^1] = p;   // dragging the point just placed
		UpdatePreview(s);
	}

	public override void Hover(PaintSession s, Vector2 p)
	{
		if (!PlacingPoints) return;
		_current = p;
		UpdatePreview(s);
	}

	public override void Up(PaintSession s)
	{
		if (!_active || ClickShape) return;

		bool tooSmall = _shape == SelectShape.Freehand
			? _points.Count < 3
			: Vector2.Distance(_start, _current) < 0.5f;
		if (tooSmall)
		{
			if (_mode == SelectMode.New) s.Deselect();   // a plain click deselects
			Reset();
			return;
		}
		Commit(s);
	}

	public override void Deactivate(PaintSession s) => Cancel();

	// ---------- finishing ----------

	// Enter, a double-click, or a click on the first point
	public void Finish(PaintSession s)
	{
		if (!PlacingPoints) return;
		if (_points.Count < 3) { Cancel(); return; }   // a selection needs an area
		Commit(s);
	}

	public void Cancel() => Reset();

	public void RemoveLastPoint(PaintSession s)
	{
		if (!PlacingPoints) return;
		if (_points.Count <= 1) { Cancel(); return; }
		_points.RemoveAt(_points.Count - 1);
		UpdatePreview(s);
	}

	private void Commit(PaintSession s)
	{
		if (s.Asset is { } asset && BuildPath(s, finished: true) is { } path)
		{
			using (path)
			{
				var shape = SelectionMask.FromPath(asset.Width, asset.Height, path);
				s.ApplySelection(shape, _mode, $"Select ({_shape.ToString().ToLowerInvariant()})");
			}
		}
		Reset();
	}

	private void Reset()
	{
		_active = false;
		_points.Clear();
		Preview?.Dispose();
		Preview = null;
	}

	// ---------- shapes ----------

	private void UpdatePreview(PaintSession s)
	{
		Preview?.Dispose();
		Preview = BuildPath(s, finished: false);
	}

	private void TrackShift(PaintSession s)
	{
		if (_shiftAtStart && !s.ShiftHeld) _shiftReleased = true;
	}

	// Shift constrains while dragging, unless it's still the Shift that chose "add"
	private bool Constrain(PaintSession s) => s.ShiftHeld && (!_shiftAtStart || _shiftReleased);

	private SKPath? BuildPath(PaintSession s, bool finished)
	{
		switch (_shape)
		{
			case SelectShape.Rectangle:
				return ShapePaths.Rect(Box(s));
			case SelectShape.Ellipse:
				return ShapePaths.Oval(Box(s));
			case SelectShape.Polygon:
				return ShapePaths.Polyline(RegularPolygon(s), closed: true);
			case SelectShape.Freehand:
				return ShapePaths.Polyline(_points, closed: true);
			default:   // Polyline, Curve: always closed, since a selection is an area
				var points = new List<Vector2>(_points);
				if (!finished && Vector2.Distance(_current, _points[^1]) > 0.01f) points.Add(_current);
				return _shape == SelectShape.Curve
					? ShapePaths.Spline(points, closed: true)
					: ShapePaths.Polyline(points, closed: true);
		}
	}

	private SKRect Box(PaintSession s)
	{
		var d = _current - _start;
		if (Constrain(s))
		{
			float m = MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y));
			d = new Vector2(MathF.CopySign(m, d.X), MathF.CopySign(m, d.Y));
		}
		var a = s.CtrlHeld ? _start - d : _start;
		var b = _start + d;
		return new SKRect(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
	}

	private List<Vector2> RegularPolygon(PaintSession s)
	{
		int n = Math.Clamp(s.PolygonSides, 3, 64);
		float radius = Vector2.Distance(_start, _current);
		float angle = MathF.Atan2(_current.Y - _start.Y, _current.X - _start.X);
		if (Constrain(s)) angle = MathF.Round(angle / (MathF.PI / 12)) * (MathF.PI / 12);   // 15° steps

		var points = new List<Vector2>(n);
		for (int i = 0; i < n; i++)
		{
			float t = angle + i * MathF.Tau / n;
			points.Add(_start + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius);
		}
		return points;
	}
}

// Photoshop's Magic Wand: click a color to select it. Uses the Tolerance slider;
// "Contiguous" = only the connected area, otherwise that color everywhere.
// Shift adds, Alt subtracts, both intersect.
public sealed class MagicWandTool : PaintTool
{
	public override string Name => "Wand";
	public override bool UsesAlt => true;

	public override void Down(PaintSession s, Vector2 p)
	{
		if (s.LayerBitmap is not { } layer || s.Asset is not { } asset) return;
		int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
		if (x < 0 || y < 0 || x >= layer.Width || y >= layer.Height) return;

		var sample = s.SampleAllLayers && s.Composite is { } merged ? PixelBuffer.Read(merged) : PixelBuffer.Read(layer);
		var bits = MagicWand.Select(sample, x, y, s.Tolerance, s.WandContiguous);
		var shape = SelectionMask.FromBits(asset.Width, asset.Height, bits);
		s.ApplySelection(shape, s.ModeForClick(), "Magic wand");
	}
}

// The wand's search, kept separate so it can be tested on its own
public static class MagicWand
{
	// Pixels that look like the one at (x, y): every channel within `tolerance`
	public static bool[] Select(PixelBuffer pixels, int x, int y, int tolerance, bool contiguous)
	{
		int w = pixels.Width, h = pixels.Height;
		var selected = new bool[w * h];
		int target = pixels.Data[y * w + x];

		if (!contiguous)
		{
			for (int i = 0; i < selected.Length; i++)
				selected[i] = Similar(pixels.Data[i], target, tolerance);
			return selected;
		}

		// Flood: spread to the four neighbours that look similar
		var visited = new bool[w * h];
		var stack = new Stack<int>();
		stack.Push(y * w + x);
		visited[y * w + x] = true;
		while (stack.Count > 0)
		{
			int i = stack.Pop();
			if (!Similar(pixels.Data[i], target, tolerance)) continue;
			selected[i] = true;

			int px = i % w, py = i / w;
			if (px > 0 && !visited[i - 1]) { visited[i - 1] = true; stack.Push(i - 1); }
			if (px < w - 1 && !visited[i + 1]) { visited[i + 1] = true; stack.Push(i + 1); }
			if (py > 0 && !visited[i - w]) { visited[i - w] = true; stack.Push(i - w); }
			if (py < h - 1 && !visited[i + w]) { visited[i + w] = true; stack.Push(i + w); }
		}
		return selected;
	}

	// Every channel (whatever the byte order) within `tolerance` of the other
	public static bool Similar(int a, int b, int tolerance)
	{
		for (int shift = 0; shift < 32; shift += 8)
			if (Math.Abs(((a >> shift) & 0xFF) - ((b >> shift) & 0xFF)) > tolerance)
				return false;
		return true;
	}
}
