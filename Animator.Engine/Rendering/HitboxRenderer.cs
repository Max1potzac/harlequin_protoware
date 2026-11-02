using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

// Hitboxes and points drawn over the object (never in exports): each kind of hitbox in its own
// colour, filled faintly; one that's off just dashed. Points as a ring with a line the way they face.
public static class HitboxRenderer
{
	public static SKColor ColorOf(string kind) => kind.Trim().ToLowerInvariant() switch
	{
		"hurt" => new SKColor(80, 200, 255),
		"hit" => new SKColor(255, 80, 80),
		"block" => new SKColor(255, 200, 60),
		"trigger" => new SKColor(180, 120, 255),
		var other => Hashed(other)
	};

	private static SKColor Hashed(string kind)
	{
		uint h = 2166136261;
		foreach (char c in kind) h = (h ^ c) * 16777619;
		return SKColor.FromHsv(h % 360, 55, 95);
	}

	public static readonly SKColor PointColor = new(120, 255, 160);

	// zoom: screen pixels per unit of the canvas (lines stay the same width on screen)
	public static void Draw(SKCanvas canvas, Skeleton skeleton, float zoom, object? selected, object? hover = null)
	{
		float px = 1f / MathF.Max(zoom, 1e-4f);
		using var dash = SKPathEffect.CreateDash(new[] { 5f * px, 4f * px }, 0);
		foreach (var hitbox in skeleton.Hitboxes)
		{
			var color = ColorOf(hitbox.Kind);
			bool picked = hitbox == selected, over = hitbox == hover;
			using var path = ShapePaths.Polyline(hitbox.Outline(), closed: true);
			using var fill = new SKPaint { Color = color.WithAlpha((byte)(hitbox.Active ? (picked ? 70 : 40) : 12)), Style = SKPaintStyle.Fill, IsAntialias = true };
			using var edge = new SKPaint
			{
				Color = color.WithAlpha((byte)(hitbox.Active ? 230 : 140)),
				Style = SKPaintStyle.Stroke,
				StrokeWidth = (picked || over ? 2.5f : 1.5f) * px,
				IsAntialias = true,
				PathEffect = hitbox.Active ? null : dash
			};
			canvas.DrawPath(path, fill);
			canvas.DrawPath(path, edge);
			if (picked) DrawHandles(canvas, hitbox, color, px);
		}

		foreach (var point in skeleton.Points)
		{
			bool picked = point == selected, over = point == hover;
			var at = point.WorldPosition;
			float angle = point.WorldAngle;
			var tip = at + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 18f * px;
			using var line = new SKPaint
			{
				Color = picked ? SKColors.White : PointColor,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = (picked || over ? 2.5f : 1.75f) * px,
				IsAntialias = true,
				StrokeCap = SKStrokeCap.Round
			};
			canvas.DrawCircle(at.X, at.Y, 5f * px, line);
			canvas.DrawLine(at.X, at.Y, tip.X, tip.Y, line);
			canvas.DrawLine(at.X - 2f * px, at.Y, at.X + 2f * px, at.Y, line);
		}
	}

	// The picked box's corner handles (drag one to resize), or a circle's edge handle
	private static void DrawHandles(SKCanvas canvas, Hitbox hitbox, SKColor color, float px)
	{
		using var handle = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
		using var ring = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * px, IsAntialias = true };
		foreach (var p in Handles(hitbox))
		{
			canvas.DrawRect(p.X - 4f * px, p.Y - 4f * px, 8f * px, 8f * px, handle);
			canvas.DrawRect(p.X - 4f * px, p.Y - 4f * px, 8f * px, 8f * px, ring);
		}
	}

	// Where its resize handles are, in the object's space: a box's corners, a circle's right edge
	public static Vector2[] Handles(Hitbox hitbox) =>
		hitbox.Shape == HitboxShape.Box ? hitbox.Outline()
		: new[] { Vector2.Transform(new Vector2(hitbox.Radius, 0f), hitbox.World) };
}
