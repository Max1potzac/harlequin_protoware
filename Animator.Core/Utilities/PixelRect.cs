namespace Animator.Core.Utilities;

// A rectangle of pixels on a picture: left, top, width and height (y down, like the picture)
public readonly record struct PixelRect(int X, int Y, int W, int H)
{
	public int Right => X + W;
	public int Bottom => Y + H;
	public bool IsEmpty => W <= 0 || H <= 0;
	public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
	public bool Intersects(PixelRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

	public static PixelRect Union(PixelRect a, PixelRect b)
	{
		int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
		return new PixelRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
	}

	// The part both cover (empty when they don't meet)
	public static PixelRect Intersect(PixelRect a, PixelRect b)
	{
		int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
		int right = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom);
		return right > x && bottom > y ? new PixelRect(x, y, right - x, bottom - y) : default;
	}

	// How many empty pixels lie between two rectangles (0 when they touch or overlap)
	public static int Gap(PixelRect a, PixelRect b)
	{
		int dx = Math.Max(0, Math.Max(a.X - b.Right, b.X - a.Right));
		int dy = Math.Max(0, Math.Max(a.Y - b.Bottom, b.Y - a.Bottom));
		return Math.Max(dx, dy);
	}
}
