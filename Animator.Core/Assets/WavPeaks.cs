namespace Animator.Core.Assets;

// A WAV file's length and loudness over time (for the waveform under the timeline): the loudest
// sample in each of `buckets` even stretches, 0..1. PCM (8, 16, 24, 32 bits) or 32-bit float;
// anything else (or not a WAV): null.
public static class WavPeaks
{
	public static (float Duration, float[] Peaks)? Read(Stream stream, int buckets)
	{
		using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
		try
		{
			if (new string(r.ReadChars(4)) != "RIFF") return null;
			r.ReadInt32();
			if (new string(r.ReadChars(4)) != "WAVE") return null;
			int format = 0, channels = 0, rate = 0, bits = 0;
			while (stream.Position + 8 <= stream.Length)
			{
				string id = new string(r.ReadChars(4));
				int size = r.ReadInt32();
				long next = stream.Position + size + (size & 1);
				if (id == "fmt ")
				{
					format = r.ReadInt16();
					channels = r.ReadInt16();
					rate = r.ReadInt32();
					r.ReadInt32();
					r.ReadInt16();
					bits = r.ReadInt16();
					if (format == 0xFFFE && size >= 26) { r.ReadInt16(); r.ReadInt16(); r.ReadInt32(); format = r.ReadInt16(); }   // extensible: the real format
				}
				else if (id == "data")
				{
					if (channels <= 0 || rate <= 0 || bits <= 0 || (format != 1 && format != 3)) return null;
					int bytes = bits / 8;
					long frames = Math.Min(size, stream.Length - stream.Position) / (bytes * channels);
					buckets = Math.Max(1, buckets);
					var peaks = new float[buckets];
					for (long f = 0; f < frames; f++)
					{
						float loudest = 0f;
						for (int c = 0; c < channels; c++)
							loudest = MathF.Max(loudest, MathF.Abs(Sample(r, format, bits)));
						int b = (int)(f * buckets / Math.Max(1, frames));
						if (loudest > peaks[b]) peaks[b] = loudest;
					}
					return (frames / (float)rate, peaks);
				}
				stream.Position = next;
			}
		}
		catch (EndOfStreamException) { }
		return null;
	}

	private static float Sample(BinaryReader r, int format, int bits) => (format, bits) switch
	{
		(3, 32) => Math.Clamp(r.ReadSingle(), -1f, 1f),
		(1, 8) => (r.ReadByte() - 128) / 128f,
		(1, 16) => r.ReadInt16() / 32768f,
		(1, 24) => ((r.ReadByte() | (r.ReadByte() << 8) | (r.ReadByte() << 16)) << 8 >> 8) / 8388608f,
		(1, 32) => r.ReadInt32() / 2147483648f,
		_ => Skip(r, bits)
	};

	private static float Skip(BinaryReader r, int bits)
	{
		r.ReadBytes(Math.Max(1, bits / 8));
		return 0f;
	}
}
