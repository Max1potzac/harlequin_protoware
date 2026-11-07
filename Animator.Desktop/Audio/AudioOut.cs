using Silk.NET.OpenAL;

namespace Animator.Desktop.Audio;

// Where sounds go: a voice per sound playing. (The tests listen with one of their own.)
public interface IAudioOut : IDisposable
{
	bool Works { get; }

	// A sound started at `from` seconds in (null: it can't be played)
	IVoice? Start(DecodedSound sound, float gain, float pitch, double from);
}

// One sound playing
public interface IVoice
{
	bool Playing { get; }
	double Position { get; }   // seconds into the sound
	float Gain { set; }
	float Pitch { set; }       // (speed and pitch together, as the Windows editor's player does)
	void Seek(double seconds);
	void Pause();
	void Resume();
	void Stop();               // for good
}

// No sound device (or no OpenAL): everything is silent
public sealed class NoAudio : IAudioOut
{
	public bool Works => false;
	public IVoice? Start(DecodedSound sound, float gain, float pitch, double from) => null;
	public void Dispose() { }
}

// OpenAL Soft (its library comes with Silk.NET for Windows, Linux and macOS): a buffer per sound,
// kept while the sound is, and a source per voice
public sealed unsafe class OpenAlOut : IAudioOut
{
	private readonly AL _al;
	private readonly ALContext _alc;
	private Device* _device;
	private Context* _context;
	private readonly Dictionary<DecodedSound, uint> _buffers = new(ReferenceEqualityComparer.Instance);

	public bool Works => _device != null;

	// The default device, or silence if there's none (or OpenAL can't be loaded)
	public static IAudioOut Open()
	{
		try
		{
			return new OpenAlOut();
		}
		catch (Exception)
		{
			return new NoAudio();
		}
	}

	private OpenAlOut()
	{
		_alc = ALContext.GetApi(true);
		_al = AL.GetApi(true);
		_device = _alc.OpenDevice(null!);
		if (_device == null) throw new InvalidOperationException("No sound device.");
		_context = _alc.CreateContext(_device, null);
		if (_context == null || !_alc.MakeContextCurrent(_context))
		{
			Dispose();
			throw new InvalidOperationException("No sound context.");
		}
	}

	public IVoice? Start(DecodedSound sound, float gain, float pitch, double from)
	{
		if (!Works || sound.Samples.Length == 0) return null;
		if (!_buffers.TryGetValue(sound, out uint buffer))
		{
			buffer = _al.GenBuffer();
			fixed (short* data = sound.Samples)
				_al.BufferData(buffer, sound.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16, data, sound.Samples.Length * sizeof(short), sound.Rate);
			_buffers[sound] = buffer;
		}
		uint source = _al.GenSource();
		_al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
		var voice = new Voice(_al, source) { Gain = gain, Pitch = pitch };
		voice.Seek(from);
		voice.Resume();
		return voice;
	}

	// A sound's buffer let go of (it isn't needed any more)
	public void Forget(DecodedSound sound)
	{
		if (_buffers.Remove(sound, out uint buffer)) _al.DeleteBuffer(buffer);
	}

	public void Dispose()
	{
		if (_context != null)
		{
			_alc.MakeContextCurrent(null);
			_alc.DestroyContext(_context);
			_context = null;
		}
		if (_device != null)
		{
			_alc.CloseDevice(_device);
			_device = null;
		}
	}

	private sealed class Voice : IVoice
	{
		private readonly AL _al;
		private uint _source;

		public Voice(AL al, uint source)
		{
			_al = al;
			_source = source;
		}

		public bool Playing
		{
			get
			{
				if (_source == 0) return false;
				_al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
				return state == (int)SourceState.Playing;
			}
		}

		public double Position
		{
			get
			{
				if (_source == 0) return 0;
				_al.GetSourceProperty(_source, SourceFloat.SecOffset, out float seconds);
				return seconds;
			}
		}

		public float Gain { set { if (_source != 0) _al.SetSourceProperty(_source, SourceFloat.Gain, Math.Clamp(value, 0f, 1f)); } }
		public float Pitch { set { if (_source != 0) _al.SetSourceProperty(_source, SourceFloat.Pitch, Math.Clamp(value, 0.1f, 4f)); } }

		public void Seek(double seconds)
		{
			if (_source != 0) _al.SetSourceProperty(_source, SourceFloat.SecOffset, (float)Math.Max(0, seconds));
		}

		public void Pause() { if (_source != 0) _al.SourcePause(_source); }
		public void Resume() { if (_source != 0) _al.SourcePlay(_source); }

		public void Stop()
		{
			if (_source == 0) return;
			_al.SourceStop(_source);
			_al.DeleteSource(_source);
			_source = 0;
		}
	}
}
