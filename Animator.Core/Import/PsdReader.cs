using System.IO.Compression;
using System.Text;

namespace Animator.Core.Import;

// One layer of a Photoshop file
public sealed class PsdLayer
{
	public string Name { get; set; } = "";
	public List<string> Groups { get; } = new();       // the groups it's in, outermost first
	public PixelRect Bounds { get; set; }               // where it sits on the canvas (it can stick out)
	public byte[] Pixels { get; set; } = Array.Empty<byte>();   // straight RGBA, row after row
	public float Opacity { get; set; } = 1f;            // its own, times its groups'
	public bool Visible { get; set; } = true;           // it and every group it's in
	public string Blend { get; set; } = "norm";         // Photoshop's four-letter blend mode key
	public bool Clipped { get; set; }                   // clipped to the layer below (a clipping mask)
	public bool HasStyle { get; set; }                  // has layer effects (shadows, strokes…), not read
}

// A Photoshop file: its canvas and its layers, bottom to top
public sealed class PsdDocument
{
	public int Width { get; init; }
	public int Height { get; init; }
	public int Depth { get; init; }                     // bits per channel: 8, 16 or 32
	public List<PsdLayer> Layers { get; } = new();
	public bool Flat { get; set; }                      // no layers: its one picture is the only layer
	public int EmptyLayers { get; set; }                // layers without pixels (adjustments, fills): left out
}

// Reads Photoshop files (.psd, and .psb "large documents"): RGB or greyscale, 8, 16 or 32 bits per
// channel, with groups, masks and clipping marks. Everything in the file is big-endian.
public static class PsdReader
{
	public static PsdDocument Read(string path) => Read(File.ReadAllBytes(path));

	public static PsdDocument Read(byte[] data)
	{
		var r = new Reader(data);
		if (r.Ascii(4) != "8BPS") throw new InvalidDataException("This isn't a Photoshop file.");
		int version = r.U16();
		if (version is not (1 or 2)) throw new InvalidDataException($"Photoshop file version {version} isn't supported.");
		r.Big = version == 2;
		r.Skip(6);
		int channels = r.U16();
		int height = checked((int)r.U32()), width = checked((int)r.U32());
		int depth = r.U16(), mode = r.U16();
		if (mode is not (1 or 3))
			throw new NotSupportedException(
				$"This Photoshop file is in {ModeName(mode)} colour mode; Harlequin reads RGB and Grayscale files. " +
				"In Photoshop, choose Image > Mode > RGB Color and save it again.");
		if (depth is not (8 or 16 or 32))
			throw new NotSupportedException($"This Photoshop file has {depth} bits per channel; Harlequin reads 8, 16 and 32.");
		var doc = new PsdDocument { Width = width, Height = height, Depth = depth };

		uint colourData = r.U32();
		r.Skip(colourData);
		uint resources = r.U32();
		r.Skip(resources);

		var records = new List<Record>();
		long section = r.Length();
		long sectionEnd = r.Position + section;
		if (section > 0)
		{
			long info = r.Length();
			long infoEnd = r.Position + info;
			if (info > 0) records = ReadLayers(r, depth, mode);
			r.Position = infoEnd;
			if (r.Position + 4 <= sectionEnd)
			{
				uint globalMask = r.U32();
				r.Skip(globalMask);
			}

			// 16- and 32-bit files keep their layers in a block of their own here
			while (r.Position + 12 <= sectionEnd)
			{
				string signature = r.Ascii(4);
				if (signature is not ("8BIM" or "8B64")) break;
				string key = r.Ascii(4);
				long length = r.Big && LongKeys.Contains(key) ? r.Length() : r.U32();
				long start = r.Position;
				if (records.Count == 0 && key is "Lr16" or "Lr32" or "Layr")
					records = ReadLayers(r, key == "Lr16" ? 16 : key == "Lr32" ? 32 : depth, mode);
				r.Position = start + ((length + 3) & ~3L);
			}
		}
		r.Position = sectionEnd;

		if (records.Count == 0)
		{
			// No layers: the picture itself
			doc.Layers.Add(new PsdLayer
			{
				Name = "Background",
				Bounds = new PixelRect(0, 0, width, height),
				Pixels = ReadComposite(r, width, height, channels, depth, mode)
			});
			doc.Flat = true;
			return doc;
		}

		// Groups: from the top down, a group's top marker comes before its layers and its bottom
		// marker after them
		var open = new List<(string Name, bool Visible, float Opacity)>();
		var layers = new List<PsdLayer>();
		for (int i = records.Count - 1; i >= 0; i--)
		{
			var rec = records[i];
			bool visible = !rec.Hidden && (open.Count == 0 || open[^1].Visible);
			float opacity = rec.Opacity / 255f * (open.Count == 0 ? 1f : open[^1].Opacity);
			if (rec.Section is 1 or 2)
			{
				open.Add((rec.Name, visible, opacity));
				continue;
			}
			if (rec.Section == 3)
			{
				if (open.Count > 0) open.RemoveAt(open.Count - 1);
				continue;
			}
			if (rec.Pixels == null)
			{
				doc.EmptyLayers++;
				continue;
			}
			var layer = new PsdLayer
			{
				Name = rec.Name,
				Bounds = rec.Bounds,
				Pixels = rec.Pixels,
				Opacity = opacity,
				Visible = visible,
				Blend = rec.Blend,
				Clipped = rec.Clipped,
				HasStyle = rec.HasStyle
			};
			layer.Groups.AddRange(open.Select(g => g.Name));
			layers.Add(layer);
		}
		layers.Reverse();   // back to bottom to top
		doc.Layers.AddRange(layers);
		return doc;
	}

	private static string ModeName(int mode) => mode switch
	{
		0 => "Bitmap",
		2 => "Indexed Color",
		4 => "CMYK",
		7 => "Multichannel",
		8 => "Duotone",
		9 => "Lab",
		_ => $"an unknown ({mode})"
	};

	// Tagged blocks whose length takes 8 bytes in a .psb
	private static readonly HashSet<string> LongKeys = new()
	{
		"LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"
	};

	private static readonly HashSet<string> BlendKeys = new()
	{
		"pass", "norm", "diss", "dark", "mul ", "idiv", "lbrn", "dkCl", "lite", "scrn", "div ", "lddg", "lgCl",
		"over", "sLit", "hLit", "vLit", "lLit", "pLit", "hMix", "diff", "smud", "fsub", "fdiv", "hue ", "sat ", "colr", "lum "
	};

	// Some programs write the key back to front ("mron")
	private static string BlendKey(string key)
	{
		if (BlendKeys.Contains(key)) return key;
		string reversed = new(key.Reverse().ToArray());
		return BlendKeys.Contains(reversed) ? reversed : "norm";
	}

	// ======================= layers =======================

	private sealed class Record
	{
		public PixelRect Bounds;
		public readonly List<(short Id, long Length)> Channels = new();
		public string Blend = "norm";
		public byte Opacity = 255;
		public bool Clipped, Hidden, HasStyle;
		public PixelRect MaskBounds;
		public byte MaskDefault;
		public bool MaskDisabled;
		public string Name = "";
		public int Section;        // 0 = a layer; 1 or 2 = a group's top (open, closed); 3 = a group's bottom
		public byte[]? Pixels;     // null = nothing to see
	}

	private static List<Record> ReadLayers(Reader r, int depth, int mode)
	{
		int count = Math.Abs((int)r.I16());   // negative: the first alpha channel is the merged picture's
		var records = new List<Record>(count);
		for (int i = 0; i < count; i++)
		{
			var rec = new Record();
			int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32();
			rec.Bounds = new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
			int channels = r.U16();
			for (int c = 0; c < channels; c++)
			{
				short id = r.I16();
				rec.Channels.Add((id, r.Length()));
			}
			r.Skip(4);   // "8BIM"
			rec.Blend = BlendKey(r.Ascii(4));
			rec.Opacity = r.U8();
			rec.Clipped = r.U8() != 0;
			rec.Hidden = (r.U8() & 2) != 0;
			r.Skip(1);
			uint extra = r.U32();
			long extraEnd = r.Position + extra;

			uint maskLength = r.U32();
			long maskEnd = r.Position + maskLength;
			if (maskLength >= 18)
			{
				int mTop = r.I32(), mLeft = r.I32(), mBottom = r.I32(), mRight = r.I32();
				rec.MaskBounds = new PixelRect(mLeft, mTop, Math.Max(0, mRight - mLeft), Math.Max(0, mBottom - mTop));
				rec.MaskDefault = r.U8();
				rec.MaskDisabled = (r.U8() & 2) != 0;
			}
			r.Position = maskEnd;
			uint ranges = r.U32();
			r.Skip(ranges);
			int nameLength = r.U8();
			rec.Name = DecodeName(r.Bytes(nameLength));
			r.Skip((4 - (1 + nameLength) % 4) % 4);   // padded to a multiple of 4

			while (r.Position + 12 <= extraEnd)
			{
				string signature = r.Ascii(4);
				if (signature is not ("8BIM" or "8B64")) break;
				string key = r.Ascii(4);
				long length = r.Big && LongKeys.Contains(key) ? r.Length() : r.U32();
				long start = r.Position;
				switch (key)
				{
					case "luni":   // the name in Unicode
						int chars = checked((int)r.U32());
						rec.Name = Encoding.BigEndianUnicode.GetString(r.Bytes(chars * 2L)).TrimEnd('\0');
						break;
					case "lsct":
					case "lsdk":   // a group marker
						rec.Section = checked((int)r.U32());
						break;
					case "lfx2":
					case "lmfx":   // layer effects ("lrFX" is there even without any)
						rec.HasStyle = true;
						break;
				}
				r.Position = start + length;
			}
			r.Position = extraEnd;
			records.Add(rec);
		}

		// Then every layer's pixels, channel after channel
		foreach (var rec in records)
			ReadPixels(r, rec, depth, mode);
		return records;
	}

	private static string DecodeName(ReadOnlySpan<byte> bytes)
	{
		try
		{
			return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			return Encoding.Latin1.GetString(bytes);   // the old one-byte names
		}
	}

	private static void ReadPixels(Reader r, Record rec, int depth, int mode)
	{
		int w = rec.Bounds.W, h = rec.Bounds.H;
		byte[]? red = null, green = null, blue = null, alpha = null, mask = null;
		foreach (var (id, length) in rec.Channels)
		{
			long start = r.Position;
			if (id is >= -2 and <= 2 && length >= 2)
			{
				var size = id == -2 ? rec.MaskBounds : rec.Bounds;
				var plane = ReadChannel(r, length, size.W, size.H, depth, colour: id >= 0);
				switch (id)
				{
					case 0: red = plane; break;
					case 1: green = plane; break;
					case 2: blue = plane; break;
					case -1: alpha = plane; break;
					case -2: mask = plane; break;
				}
			}
			r.Position = start + length;   // other channels (vector mask helpers…) aren't needed
		}
		if (w == 0 || h == 0 || red == null) return;
		if (mode == 1) green = blue = red;   // greyscale

		int count = w * h;
		var pixels = new byte[count * 4];
		for (int i = 0; i < count; i++)
		{
			pixels[i * 4] = red[i];
			pixels[i * 4 + 1] = green?[i] ?? 0;
			pixels[i * 4 + 2] = blue?[i] ?? 0;
			pixels[i * 4 + 3] = alpha?[i] ?? 255;
		}

		// A layer mask hides what's black in it (and what's outside it, if its default is black)
		if (mask != null && !rec.MaskDisabled && !rec.MaskBounds.IsEmpty)
		{
			var m = rec.MaskBounds;
			for (int y = 0; y < h; y++)
				for (int x = 0; x < w; x++)
				{
					int mx = rec.Bounds.X + x - m.X, my = rec.Bounds.Y + y - m.Y;
					int value = mx >= 0 && my >= 0 && mx < m.W && my < m.H ? mask[my * m.W + mx] : rec.MaskDefault;
					int a = (y * w + x) * 4 + 3;
					pixels[a] = (byte)((pixels[a] * value + 127) / 255);
				}
		}
		rec.Pixels = pixels;
	}

	// One channel of a layer: w × h values, made 8-bit
	private static byte[]? ReadChannel(Reader r, long length, int w, int h, int depth, bool colour)
	{
		int compression = r.U16();
		if (w <= 0 || h <= 0) return null;
		int rowBytes = w * (depth / 8);
		var raw = new byte[checked(rowBytes * h)];
		switch (compression)
		{
			case 0:
				r.Bytes(raw.Length).CopyTo(raw);
				break;
			case 1:
				var counts = new long[h];
				for (int y = 0; y < h; y++) counts[y] = r.Big ? r.U32() : r.U16();
				for (int y = 0; y < h; y++) PackBits(r.Bytes(counts[y]), raw.AsSpan(y * rowBytes, rowBytes));
				break;
			case 2:
			case 3:
				Inflate(r.Bytes(length - 2), raw);
				if (compression == 3) Unpredict(raw, w, h, depth);
				break;
			default:
				throw new InvalidDataException($"A layer is stored in an unknown way ({compression}).");
		}
		return ToEightBits(raw, w * h, depth, colour);
	}

	// The picture of a file without layers: planar channels, all compressed the same way
	private static byte[] ReadComposite(Reader r, int w, int h, int channels, int depth, int mode)
	{
		int compression = r.U16();
		int rowBytes = w * (depth / 8), planeBytes = checked(rowBytes * h);
		int wanted = Math.Min(channels, mode == 3 ? 3 : 1);
		var planes = new byte[wanted][];
		switch (compression)
		{
			case 0:
				for (int c = 0; c < wanted; c++) planes[c] = r.Bytes(planeBytes).ToArray();
				break;
			case 1:
				var counts = new long[channels * h];
				for (int i = 0; i < counts.Length; i++) counts[i] = r.Big ? r.U32() : r.U16();
				for (int c = 0; c < wanted; c++)
				{
					planes[c] = new byte[planeBytes];
					for (int y = 0; y < h; y++)
						PackBits(r.Bytes(counts[c * h + y]), planes[c].AsSpan(y * rowBytes, rowBytes));
				}
				break;
			case 2:
			case 3:
				var all = new byte[checked((long)planeBytes * channels)];
				Inflate(r.Bytes(r.Remaining), all);
				for (int c = 0; c < wanted; c++)
				{
					planes[c] = all.AsSpan(c * planeBytes, planeBytes).ToArray();
					if (compression == 3) Unpredict(planes[c], w, h, depth);
				}
				break;
			default:
				throw new InvalidDataException($"The picture is stored in an unknown way ({compression}).");
		}

		var eight = planes.Select(p => ToEightBits(p, w * h, depth, colour: true)).ToArray();
		var pixels = new byte[w * h * 4];
		for (int i = 0; i < w * h; i++)
		{
			pixels[i * 4] = eight[0][i];
			pixels[i * 4 + 1] = eight[Math.Min(1, eight.Length - 1)][i];
			pixels[i * 4 + 2] = eight[Math.Min(2, eight.Length - 1)][i];
			pixels[i * 4 + 3] = 255;
		}
		return pixels;
	}

	// PackBits: a count byte, then that many bytes as they are (0..127: n + 1 of them), or one
	// byte repeated (-1..-127: 1 - n times)
	private static void PackBits(ReadOnlySpan<byte> src, Span<byte> dst)
	{
		int i = 0, o = 0;
		while (i < src.Length && o < dst.Length)
		{
			int n = (sbyte)src[i++];
			if (n >= 0)
			{
				int count = Math.Min(n + 1, Math.Min(src.Length - i, dst.Length - o));
				src.Slice(i, count).CopyTo(dst[o..]);
				i += n + 1;
				o += count;
			}
			else if (n != -128)
			{
				if (i >= src.Length) break;
				int count = Math.Min(1 - n, dst.Length - o);
				dst.Slice(o, count).Fill(src[i++]);
				o += count;
			}
		}
	}

	private static void Inflate(ReadOnlySpan<byte> src, byte[] dst)
	{
		using var input = new MemoryStream(src.ToArray());
		using var z = new ZLibStream(input, CompressionMode.Decompress);
		int read = 0;
		while (read < dst.Length)
		{
			int n = z.Read(dst, read, dst.Length - read);
			if (n <= 0) break;
			read += n;
		}
	}

	// ZIP "with prediction" stores each value as the difference from the one before it in its row
	private static void Unpredict(byte[] data, int w, int h, int depth)
	{
		if (depth == 8)
		{
			for (int y = 0; y < h; y++)
				for (int x = 1; x < w; x++)
					data[y * w + x] += data[y * w + x - 1];
		}
		else if (depth == 16)
		{
			for (int y = 0; y < h; y++)
				for (int x = 1; x < w; x++)
				{
					int at = (y * w + x) * 2;
					int value = (data[at] << 8 | data[at + 1]) + (data[at - 2] << 8 | data[at - 1]);
					data[at] = (byte)(value >> 8);
					data[at + 1] = (byte)value;
				}
		}
		else
		{
			// 32-bit: the row's bytes are differences too, and grouped: every value's first byte,
			// then every value's second byte…
			int rowBytes = w * 4;
			var row = new byte[rowBytes];
			for (int y = 0; y < h; y++)
			{
				var span = data.AsSpan(y * rowBytes, rowBytes);
				for (int i = 1; i < rowBytes; i++) span[i] += span[i - 1];
				span.CopyTo(row);
				for (int x = 0; x < w; x++)
					for (int b = 0; b < 4; b++)
						span[x * 4 + b] = row[b * w + x];
			}
		}
	}

	private static byte[] ToEightBits(byte[] raw, int count, int depth, bool colour)
	{
		if (depth == 8) return raw;
		var eight = new byte[count];
		for (int i = 0; i < count; i++)
		{
			if (depth == 16)
			{
				int value = raw[i * 2] << 8 | raw[i * 2 + 1];
				eight[i] = (byte)((value * 255 + 32767) / 65535);
			}
			else
			{
				// 32-bit files hold linear light: colours go back to the usual (sRGB) curve
				int bits = raw[i * 4] << 24 | raw[i * 4 + 1] << 16 | raw[i * 4 + 2] << 8 | raw[i * 4 + 3];
				float value = Math.Clamp(BitConverter.Int32BitsToSingle(bits), 0f, 1f);
				if (colour) value = value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
				eight[i] = (byte)MathF.Round(value * 255f);
			}
		}
		return eight;
	}

	// ======================= reading =======================

	private sealed class Reader
	{
		private readonly byte[] _data;
		public long Position;
		public bool Big;   // a .psb: some lengths take 8 bytes

		public Reader(byte[] data) => _data = data;

		public long Remaining => Math.Max(0, _data.Length - Position);

		private int Take(long n)
		{
			if (n < 0 || Position < 0 || Position + n > _data.Length)
				throw new InvalidDataException("The Photoshop file ends too early: it may be damaged.");
			int at = (int)Position;
			Position += n;
			return at;
		}

		public byte U8() => _data[Take(1)];

		public ushort U16()
		{
			int i = Take(2);
			return (ushort)(_data[i] << 8 | _data[i + 1]);
		}

		public short I16() => (short)U16();

		public uint U32()
		{
			int i = Take(4);
			return (uint)(_data[i] << 24 | _data[i + 1] << 16 | _data[i + 2] << 8 | _data[i + 3]);
		}

		public int I32() => (int)U32();

		public long Length()
		{
			if (!Big) return U32();
			ulong high = U32();
			ulong low = U32();
			return checked((long)(high << 32 | low));
		}

		public string Ascii(int n) => Encoding.Latin1.GetString(_data, Take(n), n);

		public ReadOnlySpan<byte> Bytes(long n)
		{
			int at = Take(n);
			return new ReadOnlySpan<byte>(_data, at, (int)n);
		}

		public void Skip(long n) => Take(n);
	}
}
