namespace Animator.Desktop.Audio;

// A sound read whole: 16-bit samples, one or two channels interleaved
public sealed record DecodedSound(short[] Samples, int Channels, int Rate)
{
	public double Duration => Samples.Length / (double)Math.Max(1, Channels * Rate);
}

// Sound files read into samples: WAV (PCM 8, 16, 24, 32 bits, or 32-bit float), MP3 (NLayer) and
// Ogg Vorbis (NVorbis), all managed, the same on every system. More than two channels: the first
// two. Anything else, or a file that can't be read: null (silent).
public static class SoundDecoder
{
	public static readonly string[] Extensions = { ".wav", ".mp3", ".ogg" };

	public static DecodedSound? Read(string path)
	{
		try
		{
			using var stream = File.OpenRead(path);
			return Path.GetExtension(path).ToLowerInvariant() switch
			{
				".mp3" => Mp3(stream),
				".ogg" => Ogg(stream),
				_ => Wav(stream)
			};
		}
		catch (Exception)
		{
			return null;
		}
	}

	public static DecodedSound? Wav(Stream stream)
	{
		using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
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
				if (!((format == 1 && bits is 8 or 16 or 24 or 32) || (format == 3 && bits == 32))) return null;
				int bytes = bits / 8;
				long frames = Math.Min(size, stream.Length - stream.Position) / (bytes * channels);
				int kept = Math.Min(channels, 2);
				var samples = new short[frames * kept];
				long at = 0;
				for (long f = 0; f < frames; f++)
					for (int c = 0; c < channels; c++)
					{
						float s = Sample(r, format, bits);
						if (c < kept) samples[at++] = ToShort(s);
					}
				return new DecodedSound(samples, kept, rate);
			}
			stream.Position = next;
		}
		return null;
	}

	private static float Sample(BinaryReader r, int format, int bits) => (format, bits) switch
	{
		(3, 32) => r.ReadSingle(),
		(1, 8) => (r.ReadByte() - 128) / 128f,
		(1, 16) => r.ReadInt16() / 32768f,
		(1, 24) => ((r.ReadByte() | (r.ReadByte() << 8) | (r.ReadByte() << 16)) << 8 >> 8) / 8388608f,
		_ => r.ReadInt32() / 2147483648f
	};

	private static short ToShort(float s) => (short)Math.Clamp(MathF.Round(s * 32767f), -32768f, 32767f);

	private static DecodedSound? Mp3(Stream stream)
	{
		using var mp3 = new NLayer.MpegFile(stream);
		return FromFloats(mp3.Channels, mp3.SampleRate, (buffer, count) => mp3.ReadSamples(buffer, 0, count));
	}

	private static DecodedSound? Ogg(Stream stream)
	{
		using var vorbis = new NVorbis.VorbisReader(stream, false);
		return FromFloats(vorbis.Channels, vorbis.SampleRate, (buffer, count) => vorbis.ReadSamples(buffer, 0, count));
	}

	// Interleaved float samples, read a block at a time until there are no more
	private static DecodedSound? FromFloats(int channels, int rate, Func<float[], int, int> read)
	{
		if (channels <= 0 || rate <= 0) return null;
		int kept = Math.Min(channels, 2);
		var samples = new List<short>();
		var buffer = new float[4096 * channels];
		int got;
		while ((got = read(buffer, buffer.Length - buffer.Length % channels)) > 0)
			for (int i = 0; i < got; i++)
				if (i % channels < kept) samples.Add(ToShort(buffer[i]));
		return samples.Count == 0 ? null : new DecodedSound(samples.ToArray(), kept, rate);
	}
}
