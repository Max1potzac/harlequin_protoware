using System.Text;

namespace Animator.Tests;

// Writes small Photoshop files laid out the way Photoshop writes them, so the reader can be tested
// on groups, masks, clipping, 16-bit and large-document files (a file from another program,
// ImageMagick, is in Files too)
internal sealed class PsdTestLayer
{
	public string Name = "";
	public int Left, Top, Width, Height;
	public byte[] Rgba = Array.Empty<byte>();     // straight RGBA, Width × Height
	public byte Opacity = 255;
	public bool Hidden, Clipped, UnicodeName, Raw;
	public string Blend = "norm";
	public int Section;                           // 0 = a layer; 1 = a group's top; 3 = a group's bottom
	public (int Left, int Top, int Width, int Height, byte Outside, byte[] Values)? Mask;
}

internal static class PsdTestFile
{
	public static PsdTestLayer Solid(string name, int left, int top, int width, int height, byte r, byte g, byte b, byte a = 255) => new()
	{
		Name = name, Left = left, Top = top, Width = width, Height = height,
		Rgba = Enumerable.Range(0, width * height).SelectMany(_ => new[] { r, g, b, a }).ToArray()
	};

	public static PsdTestLayer GroupTop(string name, bool hidden = false, byte opacity = 255) =>
		new() { Name = name, Section = 1, Hidden = hidden, Opacity = opacity };

	public static PsdTestLayer GroupBottom() => new() { Name = "</Layer group>", Section = 3 };

	// layers: bottom to top, as in the file
	public static byte[] Write(int width, int height, IReadOnlyList<PsdTestLayer> layers, int depth = 8, bool psb = false)
	{
		var w = new Writer(psb);
		w.Ascii("8BPS");
		w.U16(psb ? 2 : 1);
		w.Bytes(new byte[6]);
		w.U16(3);
		w.U32((uint)height);
		w.U32((uint)width);
		w.U16(depth);
		w.U16(3);   // RGB
		w.U32(0);   // colour mode data
		w.U32(0);   // image resources

		var info = new Writer(psb);
		info.I16(layers.Count);
		var channelData = new Writer(psb);
		foreach (var layer in layers)
		{
			var channels = Channels(layer, depth, psb);
			info.I32(layer.Top);
			info.I32(layer.Left);
			info.I32(layer.Top + layer.Height);
			info.I32(layer.Left + layer.Width);
			info.U16(channels.Count);
			foreach (var (id, data) in channels)
			{
				info.I16(id);
				info.Length(data.Length);
				channelData.Bytes(data);
			}
			info.Ascii("8BIM");
			info.Ascii(layer.Blend);
			info.U8(layer.Opacity);
			info.U8(layer.Clipped ? 1 : 0);
			info.U8((layer.Hidden ? 2 : 0) | 8);
			info.U8(0);

			var extra = new Writer(psb);
			if (layer.Mask is { } m)
			{
				extra.U32(20);
				extra.I32(m.Top);
				extra.I32(m.Left);
				extra.I32(m.Top + m.Height);
				extra.I32(m.Left + m.Width);
				extra.U8(m.Outside);
				extra.U8(0);
				extra.U16(0);
			}
			else
			{
				extra.U32(0);
			}
			extra.U32(0);   // blending ranges
			var name = Encoding.Latin1.GetBytes(layer.UnicodeName ? "?" : layer.Name);
			extra.U8(name.Length);
			extra.Bytes(name);
			extra.Bytes(new byte[(4 - (1 + name.Length) % 4) % 4]);
			if (layer.UnicodeName)
			{
				var chars = Encoding.BigEndianUnicode.GetBytes(layer.Name);
				int length = 4 + chars.Length, padded = (length + 3) & ~3;
				extra.Ascii("8BIM");
				extra.Ascii("luni");
				extra.U32((uint)padded);
				extra.U32((uint)layer.Name.Length);
				extra.Bytes(chars);
				extra.Bytes(new byte[padded - length]);
			}
			if (layer.Section != 0)
			{
				extra.Ascii("8BIM");
				extra.Ascii("lsct");
				extra.U32(4);
				extra.U32((uint)layer.Section);
			}
			info.U32((uint)extra.Count);
			info.Bytes(extra.ToArray());
		}
		info.Bytes(channelData.ToArray());
		if (info.Count % 2 == 1) info.U8(0);

		var section = new Writer(psb);
		section.Length(info.Count);
		section.Bytes(info.ToArray());
		section.U32(0);   // global layer mask
		w.Length(section.Count);
		w.Bytes(section.ToArray());

		// The merged picture: plain, all white
		w.U16(0);
		w.Bytes(Enumerable.Repeat((byte)255, width * height * 3 * (depth / 8)).ToArray());
		return w.ToArray();
	}

	// Each channel: its id and its bytes (compression first)
	private static List<(short Id, byte[] Data)> Channels(PsdTestLayer layer, int depth, bool psb)
	{
		var list = new List<(short, byte[])>();
		int count = layer.Width * layer.Height;
		byte[] Plane(int c) => Enumerable.Range(0, count).Select(i => layer.Rgba[i * 4 + c]).ToArray();
		if (count == 0)
		{
			foreach (short id in new short[] { -1, 0, 1, 2 }) list.Add((id, new byte[] { 0, 0 }));
			return list;
		}
		list.Add((-1, Encode(Plane(3), layer.Width, layer.Height, depth, psb, layer.Raw)));
		for (short c = 0; c < 3; c++) list.Add((c, Encode(Plane(c), layer.Width, layer.Height, depth, psb, layer.Raw)));
		if (layer.Mask is { } m) list.Add((-2, Encode(m.Values, m.Width, m.Height, depth, psb, layer.Raw)));
		return list;
	}

	private static byte[] Encode(byte[] plane, int w, int h, int depth, bool psb, bool raw)
	{
		var samples = depth == 16 ? plane.SelectMany(v => new[] { v, v }).ToArray() : plane;   // v × 257, big-endian
		int rowBytes = w * depth / 8;
		var out_ = new Writer(psb);
		if (raw)
		{
			out_.U16(0);
			out_.Bytes(samples);
			return out_.ToArray();
		}
		out_.U16(1);
		var rows = Enumerable.Range(0, h).Select(y => PackBits(samples.AsSpan(y * rowBytes, rowBytes))).ToList();
		foreach (var row in rows)
		{
			if (psb) out_.U32((uint)row.Length);
			else out_.U16(row.Length);
		}
		foreach (var row in rows) out_.Bytes(row);
		return out_.ToArray();
	}

	// Runs of three or more equal bytes as repeats, the rest as literals
	private static byte[] PackBits(ReadOnlySpan<byte> src)
	{
		var result = new List<byte>();
		int i = 0;
		while (i < src.Length)
		{
			int run = 1;
			while (i + run < src.Length && run < 128 && src[i + run] == src[i]) run++;
			if (run >= 3)
			{
				result.Add((byte)(1 - run));
				result.Add(src[i]);
				i += run;
				continue;
			}
			int start = i;
			while (i < src.Length && i - start < 128)
			{
				if (i + 2 < src.Length && src[i] == src[i + 1] && src[i] == src[i + 2]) break;
				i++;
			}
			result.Add((byte)(i - start - 1));
			for (int k = start; k < i; k++) result.Add(src[k]);
		}
		return result.ToArray();
	}

	private sealed class Writer
	{
		private readonly List<byte> _bytes = new();
		private readonly bool _psb;
		public Writer(bool psb) => _psb = psb;
		public int Count => _bytes.Count;
		public byte[] ToArray() => _bytes.ToArray();
		public void U8(int v) => _bytes.Add((byte)v);
		public void U16(int v) { _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)v); }
		public void I16(int v) => U16((ushort)(short)v);
		public void U32(uint v) { for (int s = 24; s >= 0; s -= 8) _bytes.Add((byte)(v >> s)); }
		public void I32(int v) => U32((uint)v);
		public void Length(long v)
		{
			if (_psb) U32((uint)(v >> 32));
			U32((uint)v);
		}
		public void Ascii(string s) => _bytes.AddRange(Encoding.ASCII.GetBytes(s));
		public void Bytes(byte[] b) => _bytes.AddRange(b);
	}
}
