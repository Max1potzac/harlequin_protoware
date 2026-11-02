using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Painting;

// Pencil, Brush and Eraser are one tool with three behaviors.
//   Shift+click: straight line from where the previous stroke ended.
//   Shift+drag:  straight line from where you pressed to where you release.
public sealed class StrokeTool : PaintTool
{
	public enum Kind { Pencil, Brush, Eraser }

	public Kind ToolKind { get; }
	public override string Name => ToolKind.ToString();

	private PixelBuffer? _before;   // layer before the stroke (for undo AND rebuilding)
	private SKBitmap? _mask;        // the stroke's shape so far, drawn solid
	private Vector2 _last;

	private bool _lineMode;         // Shift was held when the stroke started
	private bool _lineDragged;      // ...and the mouse has moved since
	private Vector2 _lineStart;
	private Vector2 _downAt;

	public StrokeTool(Kind kind) => ToolKind = kind;

	public override void Down(PaintSession s, Vector2 p)
	{
		if (s.LayerBitmap is not { } layer) return;
		_before = PixelBuffer.Read(layer);
		_mask = new SKBitmap(AssetStore.InfoFor(layer.Width, layer.Height));
		_mask.Erase(SKColors.Transparent);
		_last = _downAt = p;

		_lineMode = s.ShiftHeld;
		_lineDragged = false;
		if (_lineMode)
		{
			_lineStart = s.LastPoint ?? p;   // connect to the previous stroke, if there is one
			Line(s, _lineStart, p);
		}
		else
		{
			Segment(s, p, p);
		}
	}

	public override void Move(PaintSession s, Vector2 p)
	{
		if (_mask == null) return;

		if (_lineMode)
		{
			// Once the mouse really moves, it's a Shift+drag: the line starts where you pressed
			if (!_lineDragged && Vector2.Distance(p, _downAt) > 1.5f)
			{
				_lineDragged = true;
				_lineStart = _downAt;
			}
			if (_lineDragged) Line(s, _lineStart, p);
		}
		else
		{
			Segment(s, _last, p);
		}
		_last = p;
	}

	public override void Up(PaintSession s)
	{
		if (_before != null)
		{
			s.CommitPixels($"{Name} {(_lineMode ? "line" : "stroke")}", _before);
			s.LastPoint = _last;   // the next Shift+click connects from here
		}
		_mask?.Dispose();
		_mask = null;
		_before = null;
	}

	// Rubber-band line: wipe the mask and draw only this one segment
	private void Line(PaintSession s, Vector2 a, Vector2 b)
	{
		_mask!.Erase(SKColors.Transparent);
		Segment(s, a, b);
	}

	// Adds a segment to the mask, then rebuilds the layer from it
	private void Segment(PaintSession s, Vector2 a, Vector2 b)
	{
		if (s.LayerBitmap is not { } layer || _mask == null || _before == null || s.Asset == null) return;

		using (var mc = new SKCanvas(_mask))
		{
			if (ToolKind == Kind.Pencil)
			{
				PlotSquares(mc, a, b, s.Size);
			}
			else
			{
				using var pen = new SKPaint
				{
					Color = SKColors.White,
					IsAntialias = true,
					StrokeWidth = s.Size,
					StrokeCap = SKStrokeCap.Round,
					Style = SKPaintStyle.Stroke
				};
				if (a == b)
				{
					pen.Style = SKPaintStyle.Fill;
					mc.DrawCircle(a.X, a.Y, s.Size / 2f, pen);
				}
				else
				{
					mc.DrawLine(a.X, a.Y, b.X, b.Y, pen);
				}
			}
		}

		MaskCompositor.Apply(s, layer, _before, _mask, erase: ToolKind == Kind.Eraser);
		s.Asset.MarkEdited();
	}

	// Hard-edged square "pixels" along the line: clean for pixel art
	private static void PlotSquares(SKCanvas canvas, Vector2 a, Vector2 b, int size)
	{
		using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
		int half = (size - 1) / 2;
		int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y)));

		for (int i = 0; i <= steps; i++)
		{
			var p = steps == 0 ? a : Vector2.Lerp(a, b, i / (float)steps);
			int x = (int)MathF.Floor(p.X) - half;
			int y = (int)MathF.Floor(p.Y) - half;
			canvas.DrawRect(x, y, size, size, paint);
		}
	}
}