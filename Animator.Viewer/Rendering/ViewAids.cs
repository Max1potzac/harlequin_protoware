using System.Globalization;
using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

// The grid, the guides and the rulers: editing aids in the viewport, never exported. The grid and
// guides are drawn in the units the panels measure in (the open object's, or the scene's); the
// rulers along the top and left edges count those units.
public static class ViewAids
{
	public const float RulerSize = 18f;   // the rulers' width, in screen units
	private const float MinGridGapPx = 6f;   // grid lines closer than this on screen: only every 2nd, 5th, 10th…

	public static readonly SKColor GuideColor = new(60, 220, 160);

	// ======================= grid =======================

	// The canvas is in the grid's units; area: the part of them on screen; zoom: screen pixels per unit.
	// Every fifth line is a little stronger.
	public static void DrawGrid(SKCanvas canvas, SKRect area, float size, float zoom)
	{
		if (size <= 0 || zoom <= 0) return;
		float step = size * NiceAtLeast(MinGridGapPx / (size * zoom));
		float px = 1f / zoom;
		using var minor = new SKPaint { Color = new SKColor(255, 255, 255, 20), StrokeWidth = px };
		using var major = new SKPaint { Color = new SKColor(255, 255, 255, 44), StrokeWidth = px };

		long first = (long)MathF.Floor(area.Left / step), last = (long)MathF.Ceiling(area.Right / step);
		if (last - first <= 4000)
			for (long i = first; i <= last; i++)
				canvas.DrawLine(i * step, area.Top, i * step, area.Bottom, i % 5 == 0 ? major : minor);
		first = (long)MathF.Floor(area.Top / step);
		last = (long)MathF.Ceiling(area.Bottom / step);
		if (last - first <= 4000)
			for (long i = first; i <= last; i++)
				canvas.DrawLine(area.Left, i * step, area.Right, i * step, i % 5 == 0 ? major : minor);
	}

	// 1, 2, 5, 10, 20, 50…: the smallest that's at least `atLeast` (1 at the least)
	public static float NiceAtLeast(float atLeast)
	{
		if (!(atLeast > 1)) return 1;
		return NiceStep(atLeast);
	}

	// …0.1, 0.2, 0.5, 1, 2, 5, 10…: the smallest that's at least `value`
	public static float NiceStep(float value)
	{
		if (!(value > 0) || float.IsInfinity(value)) return 1;
		float scale = MathF.Pow(10, MathF.Floor(MathF.Log10(value)));
		foreach (var m in new[] { 1f, 2f, 5f, 10f })
			if (m * scale >= value * 0.9999f) return m * scale;
		return 10 * scale;
	}

	// ======================= guides =======================

	// The canvas is in the guides' units; `active` is the one being dragged
	public static void DrawGuides(SKCanvas canvas, IEnumerable<Guide> guides, SKRect area, float zoom, Guide? active, bool locked)
	{
		using var paint = new SKPaint { Color = GuideColor.WithAlpha(locked ? (byte)120 : (byte)210), StrokeWidth = 1f / zoom };
		using var bright = new SKPaint { Color = SKColors.White, StrokeWidth = 1.5f / zoom };
		foreach (var guide in guides)
		{
			var line = guide == active ? bright : paint;
			if (guide.Vertical) canvas.DrawLine(guide.At, area.Top, guide.At, area.Bottom, line);
			else canvas.DrawLine(area.Left, guide.At, area.Right, guide.At, line);
		}
	}

	// ======================= rulers =======================

	// Along the top and the left of a view this big (screen units, the canvas in them). toUnits: a
	// screen point in the grid's units. Marks: where guides cross each ruler (screen x on the top
	// one, screen y on the left one), and where the mouse is.
	public static void DrawRulers(SKCanvas canvas, Vector2 view, Func<Vector2, Vector2> toUnits,
		IEnumerable<float> topMarks, IEnumerable<float> leftMarks, Vector2? mouse)
	{
		using var back = new SKPaint { Color = new SKColor(28, 31, 37, 240) };
		using var edge = new SKPaint { Color = new SKColor(255, 255, 255, 40), StrokeWidth = 1 };
		using var tick = new SKPaint { Color = new SKColor(200, 205, 215, 150), StrokeWidth = 1 };
		using var text = new SKPaint { Color = new SKColor(200, 205, 215), IsAntialias = true };
		using var font = new SKFont { Size = 9 };
		using var mark = new SKPaint { Color = GuideColor, IsAntialias = true };
		using var cursor = new SKPaint { Color = new SKColor(255, 90, 90, 200), StrokeWidth = 1 };

		canvas.DrawRect(0, 0, view.X, RulerSize, back);
		canvas.DrawRect(0, 0, RulerSize, view.Y, back);
		canvas.DrawLine(RulerSize, RulerSize, view.X, RulerSize, edge);
		canvas.DrawLine(RulerSize, RulerSize, RulerSize, view.Y, edge);

		// The top ruler counts across (x), the left one up and down (y)
		Ticks(canvas, true, view, toUnits, tick, text, font);
		Ticks(canvas, false, view, toUnits, tick, text, font);

		foreach (var x in topMarks)
			if (x >= RulerSize && x <= view.X) Triangle(canvas, new Vector2(x, RulerSize), true, mark);
		foreach (var y in leftMarks)
			if (y >= RulerSize && y <= view.Y) Triangle(canvas, new Vector2(RulerSize, y), false, mark);

		if (mouse is { } m)
		{
			if (m.X >= RulerSize) canvas.DrawLine(m.X, 0, m.X, RulerSize, cursor);
			if (m.Y >= RulerSize) canvas.DrawLine(0, m.Y, RulerSize, m.Y, cursor);
		}
	}

	private static void Ticks(SKCanvas canvas, bool top, Vector2 view, Func<Vector2, Vector2> toUnits,
		SKPaint tick, SKPaint text, SKFont font)
	{
		// The ruler's middle line, start to end, in units: a straight line, so units go evenly along it
		float length = (top ? view.X : view.Y) - RulerSize;
		if (length <= 1) return;
		Vector2 At(float t) => top ? new Vector2(t, RulerSize / 2) : new Vector2(RulerSize / 2, t);
		float Unit(Vector2 p) => top ? p.X : p.Y;
		float u0 = Unit(toUnits(At(RulerSize)));
		float perPx = (Unit(toUnits(At(RulerSize + length))) - u0) / length;   // units per screen unit along it
		if (MathF.Abs(perPx) < 1e-7f) return;   // turned on its side: nothing to count along it

		float label = NiceStep(MathF.Abs(perPx) * 60);   // a number every 60 screen units or more
		float minor = label / (label / 10 / MathF.Abs(perPx) >= 5 ? 10 : 5);
		float uFrom = MathF.Min(u0, u0 + perPx * length), uTo = MathF.Max(u0, u0 + perPx * length);
		long first = (long)MathF.Floor(uFrom / minor), last = (long)MathF.Ceiling(uTo / minor);
		if (last - first > 4000) return;
		int every = (int)MathF.Round(label / minor);
		string format = label >= 1 ? "0" : label >= 0.1f ? "0.0" : "0.00";

		for (long i = first; i <= last; i++)
		{
			float u = i * minor;
			float t = RulerSize + (u - u0) / perPx;
			if (t < RulerSize || t > RulerSize + length) continue;
			bool big = i % every == 0;
			float h = big ? RulerSize * 0.55f : RulerSize * 0.25f;
			if (top) canvas.DrawLine(t, RulerSize - h, t, RulerSize, tick);
			else canvas.DrawLine(RulerSize - h, t, RulerSize, t, tick);
			if (!big) continue;

			string number = u.ToString(format, CultureInfo.InvariantCulture);
			if (top)
			{
				canvas.DrawText(number, t + 2, 9, SKTextAlign.Left, font, text);
			}
			else
			{
				canvas.Save();
				canvas.RotateDegrees(-90, 9, t - 2);
				canvas.DrawText(number, 9, t - 2, SKTextAlign.Left, font, text);
				canvas.Restore();
			}
		}
	}

	// A guide's mark on a ruler: a little triangle pointing into the view
	private static void Triangle(SKCanvas canvas, Vector2 tip, bool top, SKPaint paint)
	{
		var points = top
			? new[] { tip, tip + new Vector2(-4, -6), tip + new Vector2(4, -6) }
			: new[] { tip, tip + new Vector2(-6, -4), tip + new Vector2(-6, 4) };
		using var shape = ShapePaths.Polyline(points, closed: true);
		canvas.DrawPath(shape, paint);
	}
}
