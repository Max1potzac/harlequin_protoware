using SkiaSharp;

namespace Animator.Viewer.Painting;

// How a new selection shape combines with the one you already have
// (Shift = Add, Alt = Subtract, Shift+Alt = Intersect, like Photoshop)
public enum SelectMode { New, Add, Subtract, Intersect }

// A selection of any shape: a yes / no for every pixel of the drawing. Hard-edged on purpose,
// so painting, filling and moving inside it never leaves half-selected pixels behind.
// Never changed after it's made: every edit builds a new one (that's what makes undo simple).
public sealed class SelectionMask
{
	private readonly bool[] _bits;
	private SKRegion? _region;   // built on first use: for clipping
	private SKPath? _outline;    // built on first use: for the marching ants

	public int Width { get; }
	public int Height { get; }
	public SKRectI Bounds { get; }   // the tightest box around the selected pixels
	public bool IsEmpty => Bounds.IsEmpty;

	private SelectionMask(int width, int height, bool[] bits)
	{
		Width = width;
		Height = height;
		_bits = bits;
		Bounds = FindBounds();
	}

	public bool Contains(int x, int y) =>
		x >= 0 && y >= 0 && x < Width && y < Height && _bits[y * Width + x];

	// ---------- making one ----------

	public static SelectionMask FromBits(int width, int height, bool[] bits) => new(width, height, bits);

	public static SelectionMask All(int width, int height)
	{
		var bits = new bool[width * height];
		Array.Fill(bits, true);
		return new SelectionMask(width, height, bits);
	}

	// Every pixel whose center is inside the (closed) path
	public static SelectionMask FromPath(int width, int height, SKPath path)
	{
		using var coverage = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
		coverage.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(coverage))
		using (var paint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = false })
			canvas.DrawPath(path, paint);

		byte[] bytes = coverage.Bytes;   // one byte per pixel, rows RowBytes apart
		int stride = coverage.RowBytes;
		var bits = new bool[width * height];
		for (int y = 0; y < height; y++)
			for (int x = 0; x < width; x++)
				bits[y * width + x] = bytes[y * stride + x] != 0;
		return new SelectionMask(width, height, bits);
	}

	// ---------- combining ----------

	// The result of using `shape` on the current selection (null = nothing selected).
	// With nothing selected yet, Add acts like New, and Subtract / Intersect leave nothing.
	public static SelectionMask? Combine(SelectionMask? current, SelectionMask shape, SelectMode mode)
	{
		bool fresh = current == null || current.Width != shape.Width || current.Height != shape.Height;
		if (fresh)
			return mode is SelectMode.New or SelectMode.Add && !shape.IsEmpty ? shape : null;
		if (mode == SelectMode.New) return shape.IsEmpty ? null : shape;

		var bits = new bool[shape._bits.Length];
		for (int i = 0; i < bits.Length; i++)
		{
			bool a = current!._bits[i], b = shape._bits[i];
			bits[i] = mode switch
			{
				SelectMode.Add => a || b,
				SelectMode.Subtract => a && !b,
				_ => a && b   // Intersect
			};
		}
		var result = new SelectionMask(shape.Width, shape.Height, bits);
		return result.IsEmpty ? null : result;
	}

	// Everything that isn't selected (null if that's nothing)
	public SelectionMask? Inverted()
	{
		var bits = new bool[_bits.Length];
		for (int i = 0; i < bits.Length; i++) bits[i] = !_bits[i];
		var result = new SelectionMask(Width, Height, bits);
		return result.IsEmpty ? null : result;
	}

	// The same shape moved by whole pixels; whatever moves off the drawing is dropped
	public SelectionMask Offset(int dx, int dy)
	{
		var bits = new bool[_bits.Length];
		for (int y = Bounds.Top; y < Bounds.Bottom; y++)
		{
			int ty = y + dy;
			if (ty < 0 || ty >= Height) continue;
			for (int x = Bounds.Left; x < Bounds.Right; x++)
			{
				int tx = x + dx;
				if (tx >= 0 && tx < Width && _bits[y * Width + x]) bits[ty * Width + tx] = true;
			}
		}
		return new SelectionMask(Width, Height, bits);
	}

	// ---------- drawing with it ----------

	// The selected pixels as a clip region (canvas.ClipRegion keeps painting inside it)
	public SKRegion Region => _region ??= BuildRegion();

	// The border between selected and unselected pixels, for the marching ants
	public SKPath Outline => _outline ??= Region.GetBoundaryPath();

	// One rectangle per run of selected pixels in a row, all joined together
	private SKRegion BuildRegion()
	{
		var region = new SKRegion();
		for (int y = Bounds.Top; y < Bounds.Bottom; y++)
		{
			int x = Bounds.Left;
			while (x < Bounds.Right)
			{
				while (x < Bounds.Right && !_bits[y * Width + x]) x++;
				int start = x;
				while (x < Bounds.Right && _bits[y * Width + x]) x++;
				if (x > start) region.Op(new SKRectI(start, y, x, y + 1), SKRegionOperation.Union);
			}
		}
		return region;
	}

	private SKRectI FindBounds()
	{
		int left = Width, top = Height, right = -1, bottom = -1;
		for (int y = 0; y < Height; y++)
		{
			int row = y * Width;
			for (int x = 0; x < Width; x++)
			{
				if (!_bits[row + x]) continue;
				if (x < left) left = x;
				if (x > right) right = x;
				if (y < top) top = y;
				bottom = y;
			}
		}
		return right < 0 ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
	}
}
