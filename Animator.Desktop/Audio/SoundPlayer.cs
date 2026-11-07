namespace Animator.Desktop.Audio;

// Sounds while playing in the editor, as the Windows editor's player has them: event sounds
// (several at once, each to its end) and the animation's sound track, kept in step with the
// playhead. A missing file, or one that can't be read, is simply silent. Files are read once
// (again when they change).
public sealed class SoundPlayer : IDisposable
{
	private const int MostAtOnce = 16;
	private readonly IAudioOut _out;
	private readonly Dictionary<string, (DateTime Written, DecodedSound? Sound)> _read = new();
	private readonly List<IVoice> _playing = new();
	private IVoice? _track;
	private string? _trackPath;
	private DecodedSound? _trackSound;
	private bool _trackRunning;

	public SoundPlayer(IAudioOut output) => _out = output;

	public IAudioOut Output => _out;
	public bool Muted { get; set; }
	public int PlayingCount => _playing.Count(v => v.Playing);

	// A file's sound (null: none, or it can't be read)
	public DecodedSound? Sound(string path)
	{
		DateTime written;
		try
		{
			if (!File.Exists(path)) return null;
			written = File.GetLastWriteTimeUtc(path);
		}
		catch (Exception)
		{
			return null;
		}
		if (_read.TryGetValue(path, out var known) && known.Written == written) return known.Sound;
		var sound = SoundDecoder.Read(path);
		if (known.Sound is { } old && _out is OpenAlOut al) al.Forget(old);
		_read[path] = (written, sound);
		return sound;
	}

	public void Play(string path, float volume, float pitch)
	{
		if (Muted || Sound(path) is not { } sound) return;
		_playing.RemoveAll(v =>
		{
			if (v.Playing) return false;
			v.Stop();
			return true;
		});
		if (_out.Start(sound, Math.Clamp(volume, 0f, 1f), Math.Clamp(pitch, 0.25f, 4f), 0) is not { } voice) return;
		_playing.Add(voice);
		if (_playing.Count > MostAtOnce)
		{
			_playing[0].Stop();
			_playing.RemoveAt(0);
		}
	}

	// Every frame: the sound track where the playhead says (position: seconds into the sound;
	// null path, not playing, or outside the sound: silent)
	public void SyncTrack(string? path, float volume, double position, bool playing, double speed)
	{
		if (path == null || !File.Exists(path))
		{
			CloseTrack();
			return;
		}
		if (path != _trackPath)
		{
			CloseTrack();
			_trackPath = path;
			_trackSound = Sound(path);
		}
		if (!playing || Muted || _trackSound is not { } sound || position < 0 || position >= sound.Duration)
		{
			if (_trackRunning) _track?.Pause();
			_trackRunning = false;
			return;
		}
		if (_track == null)
		{
			_track = _out.Start(sound, Math.Clamp(volume, 0f, 1f), (float)Math.Clamp(speed, 0.1, 4), position);
			_trackRunning = _track != null;
			return;
		}
		_track.Gain = Math.Clamp(volume, 0f, 1f);
		_track.Pitch = (float)Math.Clamp(speed, 0.1, 4);
		if (!_trackRunning || Math.Abs(_track.Position - position) > 0.12)
			_track.Seek(position);   // started, looped round, or drifted
		if (!_trackRunning || !_track.Playing) _track.Resume();
		_trackRunning = true;
	}

	public bool TrackRunning => _trackRunning;

	private void CloseTrack()
	{
		_track?.Stop();
		_track = null;
		_trackPath = null;
		_trackSound = null;
		_trackRunning = false;
	}

	public void StopAll()
	{
		foreach (var voice in _playing) voice.Stop();
		_playing.Clear();
		if (_trackRunning) _track?.Pause();
		_trackRunning = false;
	}

	public void Dispose()
	{
		StopAll();
		CloseTrack();
		_out.Dispose();
	}
}
