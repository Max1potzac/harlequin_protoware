namespace Animator.Core.Export;

// Packing pictures into atlas pages (the engine export): each rectangle placed on a page no
// bigger than the most allowed, with a gap round it, pages as small as they can be. MaxRects,
// best short side fit: the tried and tested way texture packers do it.
public static class AtlasPacker
{
	public readonly record struct Placed(int Index, int Page, int X, int Y, int Width, int Height);

	public sealed record Result(List<Placed> Placed, List<(int Width, int Height)> Pages);

	// sizes: the pictures (their indices are kept); gap: empty pixels between them and round the
	// edge; powerOfTwo: pages 64, 128, 256 … wide and high. A picture too big for a page throws.
	public static Result Pack(IReadOnlyList<(int Width, int Height)> sizes, int maxSize = 2048, int gap = 2, bool powerOfTwo = false)
	{
		gap = Math.Max(0, gap);
		foreach (var (w, h) in sizes)
			if (w + 2 * gap > maxSize || h + 2 * gap > maxSize)
				throw new InvalidOperationException($"A {w} × {h} picture doesn't fit a {maxSize} × {maxSize} page: allow bigger pages.");

		// Biggest first: they're the hardest to place
		var order = Enumerable.Range(0, sizes.Count)
			.OrderByDescending(i => Math.Max(sizes[i].Width, sizes[i].Height))
			.ThenByDescending(i => sizes[i].Width * sizes[i].Height)
			.ToList();
		var placed = new List<Placed>();
		var pages = new List<(int, int)>();
		while (order.Count > 0)
		{
			var page = new Page(maxSize, maxSize, gap);
			var left = new List<int>();
			foreach (int i in order)
			{
				var (w, h) = sizes[i];
				if (page.Place(w + gap, h + gap) is { } at) placed.Add(new Placed(i, pages.Count, at.X + gap, at.Y + gap, w, h));
				else left.Add(i);
			}
			if (left.Count == order.Count) throw new InvalidOperationException("Nothing fitted on a page.");   // (can't happen: sizes checked)
			int used = pages.Count;
			var onPage = placed.Where(p => p.Page == used).ToList();
			int pw = onPage.Max(p => p.X + p.Width) + gap, ph = onPage.Max(p => p.Y + p.Height) + gap;
			pages.Add(powerOfTwo ? (Pow2(pw), Pow2(ph)) : (pw, ph));
			order = left;
		}
		return new Result(placed.OrderBy(p => p.Index).ToList(), pages);
	}

	private static int Pow2(int v)
	{
		int p = 64;
		while (p < v) p *= 2;
		return p;
	}

	// One page's free rectangles (MaxRects)
	private sealed class Page
	{
		private readonly List<(int X, int Y, int W, int H)> _free = new();

		public Page(int width, int height, int gap) => _free.Add((0, 0, width - gap, height - gap));   // (the far edge keeps its gap)

		public (int X, int Y)? Place(int w, int h)
		{
			int best = -1, bestShort = int.MaxValue, bestLong = int.MaxValue;
			for (int i = 0; i < _free.Count; i++)
			{
				var f = _free[i];
				if (f.W < w || f.H < h) continue;
				int shortSide = Math.Min(f.W - w, f.H - h), longSide = Math.Max(f.W - w, f.H - h);
				if (shortSide < bestShort || (shortSide == bestShort && longSide < bestLong))
					(best, bestShort, bestLong) = (i, shortSide, longSide);
			}
			if (best < 0) return null;
			var spot = (_free[best].X, _free[best].Y, W: w, H: h);
			Split(spot);
			return (spot.X, spot.Y);
		}

		private void Split((int X, int Y, int W, int H) used)
		{
			var next = new List<(int X, int Y, int W, int H)>();
			foreach (var f in _free)
			{
				if (used.X >= f.X + f.W || used.X + used.W <= f.X || used.Y >= f.Y + f.H || used.Y + used.H <= f.Y)
				{
					next.Add(f);
					continue;
				}
				if (used.X > f.X) next.Add((f.X, f.Y, used.X - f.X, f.H));
				if (used.X + used.W < f.X + f.W) next.Add((used.X + used.W, f.Y, f.X + f.W - used.X - used.W, f.H));
				if (used.Y > f.Y) next.Add((f.X, f.Y, f.W, used.Y - f.Y));
				if (used.Y + used.H < f.Y + f.H) next.Add((f.X, used.Y + used.H, f.W, f.Y + f.H - used.Y - used.H));
			}
			// Drop the ones inside others
			_free.Clear();
			for (int i = 0; i < next.Count; i++)
			{
				var a = next[i];
				bool inside = false;
				for (int j = 0; j < next.Count && !inside; j++)
				{
					if (i == j) continue;
					var b = next[j];
					bool contained = a.X >= b.X && a.Y >= b.Y && a.X + a.W <= b.X + b.W && a.Y + a.H <= b.Y + b.H;
					inside = contained && (a != b || j < i);   // (of two the same, keep one)
				}
				if (!inside) _free.Add(a);
			}
		}
	}
}
