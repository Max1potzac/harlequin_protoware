using System.IO;
using System.Windows.Media;

namespace Animator.Viewer.Audio;

// Sounds while playing in the editor: event sounds (several at once, each to its end) and the
// animation's sound track, kept in step with the playhead. What Windows' media player plays:
// WAV, MP3, WMA, AAC / M4A. A missing file is simply silent.
public sealed class SoundPlayer
{
	private const int MostAtOnce = 16;
	private readonly List<MediaPlayer> _playing = new();
	private MediaPlayer? _track;
	private string? _trackPath;
	private bool _trackRunning;

	public bool Muted { get; set; }

	public void Play(string path, float volume, float pitch)
	{
		if (Muted || !File.Exists(path)) return;
		var player = new MediaPlayer();
		player.Open(new Uri(path, UriKind.Absolute));
		player.Volume = Math.Clamp(volume, 0f, 1f);
		player.SpeedRatio = Math.Clamp(pitch, 0.25f, 4f);
		player.MediaEnded += (_, _) => Finish(player);
		player.MediaFailed += (_, _) => Finish(player);
		player.Play();
		_playing.Add(player);
		if (_playing.Count > MostAtOnce) Finish(_playing[0]);
	}

	private void Finish(MediaPlayer player)
	{
		player.Close();
		_playing.Remove(player);
	}

	// Every frame: the sound track where the playhead says (position: seconds into the sound;
	// null path, not playing or before it starts: silent)
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
			_track = new MediaPlayer();
			_track.Open(new Uri(path, UriKind.Absolute));
			_trackPath = path;
		}
		var track = _track!;
		if (!playing || Muted || position < 0)
		{
			if (_trackRunning) track.Pause();
			_trackRunning = false;
			return;
		}
		track.Volume = Math.Clamp(volume, 0f, 1f);
		track.SpeedRatio = Math.Clamp(speed, 0.1, 4);
		if (!_trackRunning || Math.Abs(track.Position.TotalSeconds - position) > 0.12)
			track.Position = TimeSpan.FromSeconds(position);   // started, looped round, or drifted
		if (!_trackRunning) track.Play();
		_trackRunning = true;
	}

	private void CloseTrack()
	{
		_track?.Close();
		_track = null;
		_trackPath = null;
		_trackRunning = false;
	}

	public void StopAll()
	{
		foreach (var player in _playing.ToList()) Finish(player);
		if (_trackRunning) _track?.Pause();
		_trackRunning = false;
	}
}
