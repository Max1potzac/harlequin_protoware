using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Painting;

public enum FigureShape { Line, Rectangle, Ellipse, Polygon, Polyline, Curve }

// Outline and Filled use the primary color.
// OutlineAndFill: outline in the primary color, fill in the secondary (like Clip Studio).
public enum FigureStyle { Outline, Filled, OutlineAndFill }

// Clip Studio-style figure tool.
//  Drag shapes (Line, Rectangle, Ellipse, Polygon): press, drag, release.
//    Shift = constrain (15° steps, square, circle), Ctrl = from the center.
//  Click shapes (Polyline, Curve): click to add points; double-click or Enter to finish;
//    click the first point to close. Backspace removes the last point, Esc cancels.
public sealed class FigureTool : PaintTool
{
	public override string Name => "Figure";

	private PixelBuffer? _before;
	private SKBitmap? _target;      // fixed for the whole figure, even if the active layer changes
	private Asset? _asset;
	private SKBitmap? _mask;        // outline
	private SKBitmap? _fillMask;    // fill
	private FigureShape _shape;     // shape and style are fixed when the figure starts
	private FigureStyle _style;

	private Vector2 _start;
	private Vector2 _current;
	private readonly List<Vector2> _points = new();

	public IReadOnlyList<Vector2> Points => _points;
	public override bool InProgress => _before != null;
	public bool PlacingPoints => InProgress && ClickShape;

	private bool ClickShape => _shape is FigureShape.Polyline or FigureShape.Curve;

	// ---------- input ----------

	public override void Down(PaintSession s, Vector2 p)
	{
		if (!InProgress)
		{
			if (!Begin(s)) return;
			_start = _current = p;
			if (ClickShape) _points.Add(p);
			Render(s);
			return;
		}

		bool nearFirst = _points.Count >= 3 && Vector2.Distance(p, _points[0]) <= 8f / s.ViewZoom;
		if (nearFirst) { Finish(s, closed: true); return; }
		if (s.ClickCount >= 2) { Finish(s); return; }

		_points.Add(p);
		_current = p;
		Render(s);
	}

	public override void Move(PaintSession s, Vector2 p)
	{
		if (!InProgress) return;
		_current = p;
		if (ClickShape) _points[^1] = p;
		Render(s);
	}

	public override void Hover(PaintSession s, Vector2 p)
	{
		if (!InProgress || !ClickShape) return;
		_current = p;
		Render(s);
	}

	public override void Up(PaintSession s)
	{
		if (!InProgress || ClickShape) return;
		if (Vector2.Distance(_start, _current) < 0.5f) Cancel(s);
		else Commit(s);
	}

	public override void Deactivate(PaintSession s) => Finish(s);

	// ---------- finishing ----------

	public void Finish(PaintSession s, bool closed = false)
	{
		if (!InProgress) return;
		if (ClickShape && _points.Count < 2) { Cancel(s); return; }
		Render(s, finished: true, closed: closed);
		Commit(s);
	}

	public void Cancel(PaintSession s)
	{
		if (_before != null && _target != null)
		{
			_before.WriteTo(_target);
			_asset?.MarkEdited();
		}
		Reset();
	}

	public void RemoveLastPoint(PaintSession s)
	{
		if (!PlacingPoints) return;
		if (_points.Count <= 1) { Cancel(s); return; }
		_points.RemoveAt(_points.Count - 1);
		Render(s);
	}

	private bool Begin(PaintSession s)
	{
		if (s.LayerBitmap is not { } layer || s.Asset == null) return false;
		_target = layer;
		_asset = s.Asset;
		_shape = s.Figure;
		_style = s.FigureStyle;
		_before = PixelBuffer.Read(layer);
		_mask = new SKBitmap(AssetStore.InfoFor(layer.Width, layer.Height));
		_fillMask = new SKBitmap(AssetStore.InfoFor(layer.Width, layer.Height));
		_points.Clear();
		return true;
	}

	private void Commit(PaintSession s)
	{
		if (_before != null && _target != null && _asset != null)
		{
			var target = _target;
			var asset = _asset;
			var after = PixelBuffer.Read(target);
			s.Undo.Push(new SnapshotCommand<PixelBuffer>($"{_shape} on {asset.Name}", _before, after, px =>
			{
				px.WriteTo(target);
				asset.MarkEdited();
			}));
		}
		Reset();
	}

	private void Reset()
	{
		_before = null;
		_target = null;
		_asset = null;
		_mask?.Dispose();
		_mask = null;
		_fillMask?.Dispose();
		_fillMask = null;
		_points.Clear();
	}

	// ---------- drawing ----------

	private void Render(PaintSession s, bool finished = false, bool closed = false)
	{
		if (_before == null || _target == null || _mask == null || _fillMask == null || _asset == null) return;

		bool isLine = _shape == FigureShape.Line;
		bool fill = !isLine && _style != FigureStyle.Outline;
		bool outline = isLine || _style != FigureStyle.Filled;

		var passes = new List<(SKBitmap, SKColor)>();
		if (fill)
		{
			DrawShape(s, _fillMask, filled: true, finished, closed);
			passes.Add((_fillMask, _style == FigureStyle.OutlineAndFill ? s.SecondaryColor : s.Color));
		}
		if (outline)
		{
			DrawShape(s, _mask, filled: false, finished, closed);
			passes.Add((_mask, s.Color));   // drawn last, so the outline sits on top of the fill
		}

		MaskCompositor.ApplyColors(s, _target, _before, passes.ToArray());
		_asset.MarkEdited();
	}

	private void DrawShape(PaintSession s, SKBitmap mask, bool filled, bool finished, bool closed)
	{
		mask.Erase(SKColors.Transparent);
		using var c = new SKCanvas(mask);
		using var paint = new SKPaint
		{
			Color = SKColors.White,
			IsAntialias = s.FigureSmooth,
			StrokeWidth = s.Size,
			StrokeCap = SKStrokeCap.Round,
			StrokeJoin = SKStrokeJoin.Round,
			Style = filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke
		};

		switch (_shape)
		{
			case FigureShape.Line:
				var end = s.ShiftHeld ? SnapAngle(_start, _current) : _current;
				c.DrawLine(_start.X, _start.Y, end.X, end.Y, paint);
				break;

			case FigureShape.Rectangle:
				c.DrawRect(Box(s), paint);
				break;

			case FigureShape.Ellipse:
				c.DrawOval(Box(s), paint);
				break;

			case FigureShape.Polygon:
				using (var path = ShapePaths.Polyline(RegularPolygon(s), closed: true))
					c.DrawPath(path, paint);
				break;

			default:   // Polyline, Curve (a fill closes the shape automatically)
				var pts = new List<Vector2>(_points);
				if (!finished && Vector2.Distance(_current, _points[^1]) > 0.01f) pts.Add(_current);
				using (var path = _shape == FigureShape.Curve
						   ? ShapePaths.Spline(pts, closed)
						   : ShapePaths.Polyline(pts, closed))
					c.DrawPath(path, paint);
				break;
		}
	}

	private SKRect Box(PaintSession s)
	{
		var d = _current - _start;
		if (s.ShiftHeld)
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
		if (s.ShiftHeld) angle = Snap(angle);

		var pts = new List<Vector2>(n);
		for (int i = 0; i < n; i++)
		{
			float t = angle + i * MathF.Tau / n;
			pts.Add(_start + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius);
		}
		return pts;
	}

	private static float Snap(float angle)
	{
		const float step = MathF.PI / 12;   // 15 degrees
		return MathF.Round(angle / step) * step;
	}

	private static Vector2 SnapAngle(Vector2 from, Vector2 to)
	{
		var d = to - from;
		float a = Snap(MathF.Atan2(d.Y, d.X));
		return from + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d.Length();
	}
}