namespace Animator.Core.Export;

// Animated GIFs (GIF89a). Pixels come in as straight (not premultiplied) RGBA bytes, 4 per pixel.
//
// A GIF picture has at most 256 colours. One palette is shared by every frame, picked from all
// of them (median cut), so colours don't shimmer from frame to frame. Pixels less than half
// opaque become see-through: GIFs have no partial transparency.

public readonly record struct GifColor(byte R, byte G, byte B);

// Collects the colours of every frame, then picks the palette
public sealed class GifPalette
{
	// Colours are counted in boxes of 5 bits per channel (32 × 32 × 32), with exact sums,
	// so a box holding one colour gives back exactly that colour
	private const int Boxes = 32768;
	private readonly int[] _counts = new int[Boxes];
	private readonly long[] _sumR = new long[Boxes], _sumG = new long[Boxes], _sumB = new long[Boxes];
	private int[] _lookup = Array.Empty<int>();   // box → palette index (-1 = not worked out yet)

	public bool HasTransparency { get; private set; }
	public GifColor[] Colors { get; private set; } = Array.Empty<GifColor>();

	private static int BoxOf(int r, int g, int b) => (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);

	// step > 1 looks at every step-th pixel only (big frames)
	public void Add(byte[] rgba, int step = 1)
	{
		step = Math.Max(1, step);
		for (int i = 0; i + 3 < rgba.Length; i += 4 * step)
		{
			if (rgba[i + 3] < 128)
			{
				HasTransparency = true;
				continue;
			}
			int box = BoxOf(rgba[i], rgba[i + 1], rgba[i + 2]);
			_counts[box]++;
			_sumR[box] += rgba[i];
			_sumG[box] += rgba[i + 1];
			_sumB[box] += rgba[i + 2];
		}
	}

	// Up to `max` colours (255 leaves the last index for "see-through"). Median cut: the box of
	// colours spanning the widest range is split at its middle, until there are enough.
	public void Build(int max = 255)
	{
		var used = new List<int>();
		for (int i = 0; i < Boxes; i++)
			if (_counts[i] > 0) used.Add(i);

		var groups = new List<List<int>>();
		if (used.Count > 0) groups.Add(used);
		while (groups.Count < max)
		{
			int best = -1, bestRange = 0;
			long bestCount = 0;
			for (int k = 0; k < groups.Count; k++)
			{
				if (groups[k].Count < 2) continue;
				int range = Range(groups[k], out _);
				long count = groups[k].Sum(b => (long)_counts[b]);
				if (range > bestRange || (range == bestRange && count > bestCount))
				{
					best = k;
					bestRange = range;
					bestCount = count;
				}
			}
			if (best < 0) break;   // every group is a single colour box
			var (low, high) = Split(groups[best]);
			groups[best] = low;
			groups.Add(high);
		}

		Colors = groups.Select(Average).DefaultIfEmpty(new GifColor(0, 0, 0)).ToArray();
		_lookup = new int[Boxes];
		Array.Fill(_lookup, -1);
		for (int k = 0; k < groups.Count; k++)
			foreach (int box in groups[k]) _lookup[box] = k;
	}

	// The palette index for a colour: the group its box went into, or else the nearest colour
	public int IndexOf(int r, int g, int b)
	{
		int box = BoxOf(r, g, b);
		int index = _lookup[box];
		if (index >= 0) return index;

		// A colour the frames didn't have (a dithered one, or a pixel that wasn't sampled):
		// the nearest palette colour to the middle of its box
		int cr = (r & ~7) + 4, cg = (g & ~7) + 4, cb = (b & ~7) + 4;
		int bestDistance = int.MaxValue;
		for (int i = 0; i < Colors.Length; i++)
		{
			int dr = Colors[i].R - cr, dg = Colors[i].G - cg, db = Colors[i].B - cb;
			int d = dr * dr + dg * dg + db * db;
			if (d < bestDistance)
			{
				bestDistance = d;
				index = i;
			}
		}
		_lookup[box] = index;
		return index;
	}

	private static int Channel(int box, int c) => c switch { 0 => box >> 10, 1 => (box >> 5) & 31, _ => box & 31 };

	private static int Range(List<int> group, out int axis)
	{
		axis = 0;
		int best = -1;
		for (int c = 0; c < 3; c++)
		{
			int min = 31, max = 0;
			foreach (int box in group)
			{
				int v = Channel(box, c);
				if (v < min) min = v;
				if (v > max) max = v;
			}
			if (max - min > best)
			{
				best = max - min;
				axis = c;
			}
		}
		return best;
	}

	// Splits along the widest channel where half the pixels are on each side
	private (List<int> Low, List<int> High) Split(List<int> group)
	{
		Range(group, out int axis);
		var sorted = group.OrderBy(b => Channel(b, axis)).ToList();
		long total = sorted.Sum(b => (long)_counts[b]), running = 0;
		int cut = 1;
		for (int i = 0; i < sorted.Count - 1; i++)
		{
			running += _counts[sorted[i]];
			cut = i + 1;
			if (running * 2 >= total) break;
		}
		return (sorted.GetRange(0, cut), sorted.GetRange(cut, sorted.Count - cut));
	}

	private GifColor Average(List<int> group)
	{
		long n = 0, r = 0, g = 0, b = 0;
		foreach (int box in group)
		{
			n += _counts[box];
			r += _sumR[box];
			g += _sumG[box];
			b += _sumB[box];
		}
		return n == 0 ? new GifColor(0, 0, 0) : new GifColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
	}
}

// Writes the file: the header and palette first, then each frame as it comes, then the end
public sealed class GifWriter : IDisposable
{
	private const int SeeThrough = 255;   // the palette entry kept for see-through pixels

	private readonly Stream _out;
	private readonly int _width, _height;
	private readonly GifPalette _palette;
	private readonly bool _transparent, _dither;
	private bool _done;

	public GifWriter(Stream output, int width, int height, GifPalette palette, bool loop, bool dither)
	{
		_out = output;
		_width = width;
		_height = height;
		_palette = palette;
		_transparent = palette.HasTransparency;
		_dither = dither;

		Write("GIF89a");
		Short(width);
		Short(height);
		_out.WriteByte(0xF7);   // a global palette of 256 entries, 8 bits per channel
		_out.WriteByte(_transparent ? (byte)SeeThrough : (byte)0);   // background colour index
		_out.WriteByte(0);      // square pixels

		for (int i = 0; i < 256; i++)
		{
			var c = i < palette.Colors.Length ? palette.Colors[i] : new GifColor(0, 0, 0);
			_out.WriteByte(c.R);
			_out.WriteByte(c.G);
			_out.WriteByte(c.B);
		}

		if (loop)
		{
			// NETSCAPE2.0: play forever
			_out.WriteByte(0x21);
			_out.WriteByte(0xFF);
			_out.WriteByte(11);
			Write("NETSCAPE2.0");
			_out.WriteByte(3);
			_out.WriteByte(1);
			Short(0);
			_out.WriteByte(0);
		}
	}

	// delay: how long the frame shows, in hundredths of a second
	public void AddFrame(byte[] rgba, int delay)
	{
		// Graphic control: the delay, the see-through entry, and clearing the frame before the
		// next one (so see-through parts don't show the previous frame)
		_out.WriteByte(0x21);
		_out.WriteByte(0xF9);
		_out.WriteByte(4);
		_out.WriteByte(_transparent ? (byte)((2 << 2) | 1) : (byte)(1 << 2));
		Short(Math.Clamp(delay, 0, ushort.MaxValue));
		_out.WriteByte(_transparent ? (byte)SeeThrough : (byte)0);
		_out.WriteByte(0);

		// The picture covers the whole canvas, with the global palette
		_out.WriteByte(0x2C);
		Short(0);
		Short(0);
		Short(_width);
		Short(_height);
		_out.WriteByte(0);

		WriteLzw(_out, Indices(rgba));
	}

	// Palette indices for every pixel. Dithering (Floyd–Steinberg) passes each pixel's colour
	// error on to the pixels right of it and below, so gradients come out smoother.
	private byte[] Indices(byte[] rgba)
	{
		var indices = new byte[_width * _height];
		var here = new float[(_width + 2) * 3];    // errors for this row, one pixel of margin each side
		var below = new float[(_width + 2) * 3];   // and for the next one

		for (int y = 0; y < _height; y++)
		{
			for (int x = 0; x < _width; x++)
			{
				int p = (y * _width + x) * 4;
				if (_transparent && rgba[p + 3] < 128)
				{
					indices[y * _width + x] = SeeThrough;
					continue;
				}

				int e = (x + 1) * 3;
				int r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
				if (_dither)
				{
					r = Math.Clamp((int)MathF.Round(r + here[e]), 0, 255);
					g = Math.Clamp((int)MathF.Round(g + here[e + 1]), 0, 255);
					b = Math.Clamp((int)MathF.Round(b + here[e + 2]), 0, 255);
				}

				int index = _palette.IndexOf(r, g, b);
				indices[y * _width + x] = (byte)index;
				if (!_dither) continue;

				var c = _palette.Colors[index];
				Spread(here, below, e, r - c.R, g - c.G, b - c.B);
			}
			if (_dither)
			{
				(here, below) = (below, here);
				Array.Clear(below);
			}
		}
		return indices;
	}

	private static void Spread(float[] here, float[] below, int e, float red, float green, float blue)
	{
		for (int ch = 0; ch < 3; ch++)
		{
			float error = ch == 0 ? red : ch == 1 ? green : blue;
			here[e + 3 + ch] += error * 7 / 16;
			below[e - 3 + ch] += error * 3 / 16;
			below[e + ch] += error * 5 / 16;
			below[e + 3 + ch] += error * 1 / 16;
		}
	}

	// GIF's LZW: 8-bit pixels, codes from 9 up to 12 bits, packed low bits first into
	// blocks of up to 255 bytes
	private static void WriteLzw(Stream s, byte[] indices)
	{
		const int minCodeSize = 8;
		s.WriteByte(minCodeSize);
		var bits = new BlockPacker(s);

		int clear = 1 << minCodeSize, end = clear + 1;
		int codeSize = minCodeSize + 1, next = end + 1;
		var codes = new Dictionary<int, int>();

		bits.Write(clear, codeSize);
		int prefix = indices.Length > 0 ? indices[0] : 0;
		for (int i = 1; i < indices.Length; i++)
		{
			int c = indices[i];
			int key = (prefix << 8) | c;
			if (codes.TryGetValue(key, out int code))
			{
				prefix = code;
				continue;
			}

			bits.Write(prefix, codeSize);
			if (next < 4096)
			{
				codes[key] = next++;
				// The reader's table is one entry behind, so it grows a bit as soon as it reaches this
				if (next > (1 << codeSize) && codeSize < 12) codeSize++;
			}
			else
			{
				bits.Write(clear, codeSize);   // the table is full: start over
				codes.Clear();
				codeSize = minCodeSize + 1;
				next = end + 1;
			}
			prefix = c;
		}
		bits.Write(prefix, codeSize);
		// Reading that last code fills one more entry, which may widen the end code
		if (next >= (1 << codeSize) && codeSize < 12) codeSize++;
		bits.Write(end, codeSize);
		bits.Flush();
		s.WriteByte(0);   // no more blocks
	}

	private sealed class BlockPacker
	{
		private readonly Stream _s;
		private readonly byte[] _block = new byte[255];
		private int _length, _count;
		private long _bits;

		public BlockPacker(Stream s) => _s = s;

		public void Write(int code, int size)
		{
			_bits |= (long)code << _count;
			_count += size;
			while (_count >= 8)
			{
				Byte((byte)(_bits & 0xFF));
				_bits >>= 8;
				_count -= 8;
			}
		}

		public void Flush()
		{
			if (_count > 0) Byte((byte)(_bits & 0xFF));
			_bits = 0;
			_count = 0;
			if (_length > 0)
			{
				_s.WriteByte((byte)_length);
				_s.Write(_block, 0, _length);
				_length = 0;
			}
		}

		private void Byte(byte b)
		{
			_block[_length++] = b;
			if (_length < 255) return;
			_s.WriteByte(255);
			_s.Write(_block, 0, 255);
			_length = 0;
		}
	}

	private void Short(int v)
	{
		_out.WriteByte((byte)(v & 0xFF));
		_out.WriteByte((byte)((v >> 8) & 0xFF));
	}

	private void Write(string ascii)
	{
		foreach (char c in ascii) _out.WriteByte((byte)c);
	}

	// The end of the file. The stream itself stays open (whoever made it closes it).
	public void Dispose()
	{
		if (_done) return;
		_done = true;
		_out.WriteByte(0x3B);
		_out.Flush();
	}

	// Frame delays for a frame rate, in hundredths of a second, rounded so they add up right
	// over time (at 30 fps: 3, 4, 3, 3, 4, 3 …). GIF players can't show faster than 2.
	public static int Delay(int frame, float framesPerSecond) =>
		Math.Max(2, (int)MathF.Round((frame + 1) * 100f / framesPerSecond) - (int)MathF.Round(frame * 100f / framesPerSecond));
}
