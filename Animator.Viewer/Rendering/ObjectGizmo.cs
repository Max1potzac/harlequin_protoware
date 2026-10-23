using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

// Handle positions for an object in the scene, in scene space
public sealed record ObjectHandles(Vector2[] Corners, Vector2 Top, Vector2 Rotate, Vector2 Origin);

// The green box around the selected object at scene level: drag inside to move it, a corner
// to scale it, the round handle to turn it. Turning and scaling happen around its origin.
public static class ObjectGizmo
{
	private const float RotateHandleDistancePx = 28f;
	private static readonly SKColor Green = new(110, 220, 120);

	public static ObjectHandles For(Scene scene, SceneObject obj, SkeletonRenderer renderer, float zoom)
	{
		var box = LocalBounds(obj, renderer);
		var m = scene.WorldTransform(obj);
		Vector2 P(float x, float y) => Vector2.Transform(new Vector2(x, y), m);

		var corners = new[] { P(box.Left, box.Bottom), P(box.Right, box.Bottom), P(box.Right, box.Top), P(box.Left, box.Top) };
		var top = P((box.Left + box.Right) / 2, box.Bottom);   // y-up: Bottom is the higher edge
		var center = P((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
		var dir = top - center;
		dir = dir.LengthSquared() > 1e-6f ? Vector2.Normalize(dir) : Vector2.UnitY;
		return new ObjectHandles(corners, top, top + dir * (RotateHandleDistancePx / zoom), P(0, 0));
	}

	// Everything the object shows, in its own space: its parts and bones (a small box if empty)
	public static SKRect LocalBounds(SceneObject obj, SkeletonRenderer renderer)
	{
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		void Add(float x, float y)
		{
			minX = MathF.Min(minX, x);
			minY = MathF.Min(minY, y);
			maxX = MathF.Max(maxX, x);
			maxY = MathF.Max(maxY, y);
		}

		if (renderer.ImageBounds(obj.Skeleton) is { } images)
		{
			Add(images.Left, images.Top);
			Add(images.Right, images.Bottom);
		}
		foreach (var bone in obj.Skeleton.AllBones())
		{
			var a = bone.World.Translation;
			var b = bone.TipWorld;
			Add(a.X, a.Y);
			Add(b.X, b.Y);
		}

		if (minX > maxX) return new SKRect(-20, -20, 20, 20);   // nothing at all yet
		if (maxX - minX < 2) { minX -= 10; maxX += 10; }          // a single point or a flat line
		if (maxY - minY < 2) { minY -= 10; maxY += 10; }
		return new SKRect(minX, minY, maxX, maxY);
	}

	public static void Draw(SKCanvas canvas, ObjectHandles h, float zoom)
	{
		float px = 1f / zoom;
		using var line = new SKPaint { Color = Green, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * px, IsAntialias = true };
		using var fill = new SKPaint { Color = Green, IsAntialias = true };

		for (int i = 0; i < 4; i++)
		{
			var a = h.Corners[i];
			var b = h.Corners[(i + 1) % 4];
			canvas.DrawLine(a.X, a.Y, b.X, b.Y, line);
		}
		float s = 4 * px;
		foreach (var c in h.Corners) canvas.DrawRect(c.X - s, c.Y - s, s * 2, s * 2, fill);
		canvas.DrawLine(h.Top.X, h.Top.Y, h.Rotate.X, h.Rotate.Y, line);
		canvas.DrawCircle(h.Rotate.X, h.Rotate.Y, 5 * px, fill);

		// The origin: what the object turns and scales around
		canvas.DrawLine(h.Origin.X - 8 * px, h.Origin.Y, h.Origin.X + 8 * px, h.Origin.Y, line);
		canvas.DrawLine(h.Origin.X, h.Origin.Y - 8 * px, h.Origin.X, h.Origin.Y + 8 * px, line);
	}
}
