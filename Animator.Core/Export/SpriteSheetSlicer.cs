namespace Animator.Core.Export;

// A rectangle of pixels on a picture: left, top, width and height (y down, like the picture)
public readonly record struct PixelRect(int X, int Y, int W, int H)
{
	public int Right => X + W;
	public int Bottom => Y + H;
	public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
	public bool Intersects(PixelRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

	public static PixelRect Union(PixelRect a, PixelRect b)
	{
		int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
		return new PixelRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
	}

	// How many empty pixels lie between two rectangles (0 when they touch or overlap)
	public static int Gap(PixelRect a, PixelRect b)
	{
		int dx = Math.Max(0, Math.Max(a.X - b.Right, b.X - a.Right));
		int dy = Math.Max(0, Math.Max(a.Y - b.Bottom, b.Y - a.Bottom));
		return Math.Max(dx, dy);
	}
}

// Cuts a sprite sheet into pieces. Pictures come in as their alpha (opacity) only: one byte per
// pixel, row after row.
public static class SpriteSheetSlicer
{
	// The cells of a grid: cells of one size, the first one `offset` pixels in, `gap` pixels
	// apart. Cells that are completely see-through are skipped (when the alpha is given).
	public static List<PixelRect> Grid(int width, int height, int cellWidth, int cellHeight,
		int offsetX = 0, int offsetY = 0, int gapX = 0, int gapY = 0, byte[]? alpha = null)
	{
		var cells = new List<PixelRect>();
		if (cellWidth <= 0 || cellHeight <= 0) return cells;
		for (int y = offsetY; y + cellHeight <= height; y += cellHeight + Math.Max(0, gapY))
			for (int x = offsetX; x + cellWidth <= width; x += cellWidth + Math.Max(0, gapX))
			{
				var cell = new PixelRect(x, y, cellWidth, cellHeight);
				if (alpha == null || !Empty(alpha, width, cell)) cells.Add(cell);
			}
		return cells;
	}

	// Separate sprites: patches of visible pixels, with patches closer than `gap` pixels counted
	// as one sprite (a detached eye or a spark stays with its body), and anything smaller than
	// `smallest` pixels both ways left out (specks). In reading order.
	public static List<PixelRect> Find(byte[] alpha, int width, int height, int gap = 2, int smallest = 3)
	{
		var seen = new bool[width * height];
		var boxes = new List<PixelRect>();
		var stack = new Stack<int>();

		for (int start = 0; start < seen.Length; start++)
		{
			if (alpha[start] == 0 || seen[start]) continue;

			// Every visible pixel touching this one (corners too)
			int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
			seen[start] = true;
			stack.Push(start);
			while (stack.Count > 0)
			{
				int i = stack.Pop();
				int x = i % width, y = i / width;
				minX = Math.Min(minX, x);
				minY = Math.Min(minY, y);
				maxX = Math.Max(maxX, x);
				maxY = Math.Max(maxY, y);
				for (int dy = -1; dy <= 1; dy++)
					for (int dx = -1; dx <= 1; dx++)
					{
						int nx = x + dx, ny = y + dy;
						if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
						int n = ny * width + nx;
						if (alpha[n] == 0 || seen[n]) continue;
						seen[n] = true;
						stack.Push(n);
					}
			}
			boxes.Add(new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1));
		}

		// Patches near each other are one sprite. A box that grows can reach new neighbours,
		// so passes repeat until one merges nothing.
		int within = Math.Max(1, gap);
		bool merged = true;
		while (merged)
		{
			merged = false;
			for (int a = 0; a < boxes.Count; a++)
				for (int b = boxes.Count - 1; b > a; b--)
				{
					if (PixelRect.Gap(boxes[a], boxes[b]) >= within) continue;
					boxes[a] = PixelRect.Union(boxes[a], boxes[b]);
					boxes.RemoveAt(b);
					merged = true;
				}
		}

		return ReadingOrder(boxes.Where(r => r.W >= smallest || r.H >= smallest));
	}

	// Left to right, top to bottom
	public static List<PixelRect> ReadingOrder(IEnumerable<PixelRect> rects) =>
		Rows(rects, r => r).SelectMany(row => row).ToList();

	// Rows from the top, each from the left. Something belongs to a row when its middle is level
	// with the row's first (topmost) one.
	public static List<List<T>> Rows<T>(IEnumerable<T> items, Func<T, PixelRect> rectOf)
	{
		var rest = items.OrderBy(i => rectOf(i).Y).ThenBy(i => rectOf(i).X).ToList();
		var rows = new List<List<T>>();
		while (rest.Count > 0)
		{
			var first = rectOf(rest[0]);
			var row = new List<T>();
			var below = new List<T>();
			foreach (var item in rest)
			{
				var r = rectOf(item);
				int middle = r.Y + r.H / 2;
				if (row.Count == 0 || middle >= first.Y && middle < first.Bottom) row.Add(item);
				else below.Add(item);
			}
			rows.Add(row.OrderBy(i => rectOf(i).X).ToList());
			rest = below;
		}
		return rows;
	}

	// No visible pixel inside the rectangle
	public static bool Empty(byte[] alpha, int width, PixelRect r)
	{
		for (int y = r.Y; y < r.Bottom; y++)
			for (int x = r.X; x < r.Right; x++)
				if (alpha[y * width + x] != 0) return false;
		return true;
	}
}
