using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Painting;

// Shared by every tool that draws its shape into a mask first:
// the layer becomes "original pixels + mask(s) applied once".
internal static class MaskCompositor
{
	// One mask, in the primary color (or as an eraser)
	public static void Apply(PaintSession s, SKBitmap layer, PixelBuffer before, SKBitmap mask, bool erase)
	{
		if (!erase)
		{
			ApplyColors(s, layer, before, (mask, s.Color));
			return;
		}

		before.WriteTo(layer);
		using var canvas = new SKCanvas(layer);
		using var paint = new SKPaint { BlendMode = SKBlendMode.DstOut };
		s.ClipToSelection(canvas);
		canvas.DrawBitmap(mask, 0, 0, AssetStore.Nearest, paint);
	}

	// Several masks, each in its own color, applied in order (first = bottom)
	public static void ApplyColors(PaintSession s, SKBitmap layer, PixelBuffer before,
								   params (SKBitmap Mask, SKColor Color)[] passes)
	{
		before.WriteTo(layer);
		using var canvas = new SKCanvas(layer);
		s.ClipToSelection(canvas);

		foreach (var (mask, color) in passes)
		{
			using var paint = new SKPaint
			{
				ColorFilter = SKColorFilter.CreateBlendMode(color.WithAlpha(255), SKBlendMode.SrcIn),
				Color = SKColors.White.WithAlpha(color.Alpha)
			};
			canvas.DrawBitmap(mask, 0, 0, AssetStore.Nearest, paint);
		}
	}
}

internal static class ShapePaths
{
	// Your SkiaSharp version marks SKPath's building methods as obsolete in favor of a newer
	// builder class. They still work, so we silence that warning for just this block.
#pragma warning disable CS0618

	public static SKPath Polyline(IReadOnlyList<Vector2> points, bool closed)
	{
		var path = new SKPath();
		if (points.Count == 0) return path;
		path.MoveTo(points[0].X, points[0].Y);
		for (int i = 1; i < points.Count; i++) path.LineTo(points[i].X, points[i].Y);
		if (closed) path.Close();
		return path;
	}

	// A smooth curve passing through every point (Catmull-Rom spline as cubic Béziers)
	public static SKPath Spline(IReadOnlyList<Vector2> points, bool closed)
	{
		int n = points.Count;
		if (n < 3) return Polyline(points, closed);

		Vector2 P(int i) => closed ? points[((i % n) + n) % n] : points[Math.Clamp(i, 0, n - 1)];

		var path = new SKPath();
		path.MoveTo(points[0].X, points[0].Y);
		int segments = closed ? n : n - 1;
		for (int i = 0; i < segments; i++)
		{
			var p0 = P(i - 1);
			var p1 = P(i);
			var p2 = P(i + 1);
			var p3 = P(i + 2);
			var c1 = p1 + (p2 - p0) / 6f;
			var c2 = p2 - (p3 - p1) / 6f;
			path.CubicTo(c1.X, c1.Y, c2.X, c2.Y, p2.X, p2.Y);
		}
		if (closed) path.Close();
		return path;
	}

#pragma warning restore CS0618
}