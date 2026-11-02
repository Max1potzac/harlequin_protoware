using SkiaSharp;

namespace Animator.Viewer.Rendering;

// The grey squares behind a picture, so its see-through pixels show as see-through
public static class Checkerboard
{
	private static readonly SKColor Light = new(204, 204, 204);
	private static readonly SKColor Dark = new(158, 158, 158);

	// Fills `picture` (in view units) with squares, only as much of it as the view (0, 0, viewW,
	// viewH) shows. The squares keep their size at any zoom and move with the picture.
	public static void Draw(SKCanvas canvas, SKRect picture, float viewW, float viewH, float square = 8)
	{
		var area = SKRect.Intersect(picture, new SKRect(0, 0, viewW, viewH));
		if (area.IsEmpty) return;
		using var light = new SKPaint { Color = Light };
		using var dark = new SKPaint { Color = Dark };
		canvas.DrawRect(area, light);

		int cx0 = (int)((area.Left - picture.Left) / square), cy0 = (int)((area.Top - picture.Top) / square);
		canvas.Save();
		canvas.ClipRect(area);
		for (int cy = cy0; picture.Top + cy * square < area.Bottom; cy++)
			for (int cx = cx0; picture.Left + cx * square < area.Right; cx++)
				if ((cx + cy) % 2 == 1)
					canvas.DrawRect(SKRect.Create(picture.Left + cx * square, picture.Top + cy * square, square, square), dark);
		canvas.Restore();
	}
}
