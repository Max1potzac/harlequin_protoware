using Animator.Core.Keyframing;
using Animator.Desktop.Audio;
using Animator.Viewer.Files;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// What the tests' windows play sounds to: each sound started, and what happened to it since
internal sealed class ListeningAudio : IAudioOut
{
	internal sealed class Voice : IVoice
	{
		public required DecodedSound Sound { get; init; }
		public float GainNow { get; private set; }
		public float PitchNow { get; private set; }
		public bool Stopped { get; private set; }
		public bool Paused { get; private set; }
		public double At { get; set; }

		public bool Playing => !Stopped && !Paused && At < Sound.Duration;
		public double Position => At;
		public float Gain { set => GainNow = value; }
		public float Pitch { set => PitchNow = value; }
		public void Seek(double seconds) => At = seconds;
		public void Pause() => Paused = true;
		public void Resume() => Paused = false;
		public void Stop() => Stopped = true;
	}

	public List<Voice> Started { get; } = new();
	public bool Works => true;

	public IVoice? Start(DecodedSound sound, float gain, float pitch, double from)
	{
		var voice = new Voice { Sound = sound, At = from };
		voice.Gain = gain;
		voice.Pitch = pitch;
		Started.Add(voice);
		return voice;
	}

	public void Dispose() { }
}

// Sound: files read, event sounds and the sound track heard while playing, the sound map, and
// the events and sound map files
public class SoundTests
{
	// A WAV of a tone: seconds long, at this rate, with these channels and bits
	internal static string Wav(string folder, string name, double seconds, int rate = 8000, int channels = 1, int bits = 16)
	{
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, name);
		int frames = (int)(seconds * rate), bytes = bits / 8;
		using var w = new BinaryWriter(File.Create(path));
		w.Write("RIFF"u8);
		w.Write(36 + frames * channels * bytes);
		w.Write("WAVEfmt "u8);
		w.Write(16);
		w.Write((short)(bits == 32 ? 3 : 1));
		w.Write((short)channels);
		w.Write(rate);
		w.Write(rate * channels * bytes);
		w.Write((short)(channels * bytes));
		w.Write((short)bits);
		w.Write("data"u8);
		w.Write(frames * channels * bytes);
		for (int f = 0; f < frames; f++)
			for (int c = 0; c < channels; c++)
			{
				float s = 0.5f * MathF.Sin(f * 2 * MathF.PI * 440 / rate);
				switch (bits)
				{
					case 16: w.Write((short)(s * 32767)); break;
					case 24: int v = (int)(s * 8388607); w.Write((byte)v); w.Write((byte)(v >> 8)); w.Write((byte)(v >> 16)); break;
					case 32: w.Write(s); break;
					default: w.Write((byte)(s * 127 + 128)); break;
				}
			}
		return path;
	}

	private static string Folder() => Path.Combine(Path.GetTempPath(), "hq-sound-" + Guid.NewGuid().ToString("N"));

	private static ListeningAudio Ears(MainWindow window) => (ListeningAudio)window.Sound.Output;

	private static void Finish(Task task)
	{
		var until = DateTime.UtcNow.AddSeconds(30);
		while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		Assert.True(task.IsCompleted);
		task.GetAwaiter().GetResult();
	}

	[Fact]
	public void WAV_files_are_read_in_their_kinds()
	{
		string folder = Folder();
		var mono = SoundDecoder.Read(Wav(folder, "a.wav", 0.5))!;
		Assert.Equal(1, mono.Channels);
		Assert.Equal(8000, mono.Rate);
		Assert.Equal(0.5, mono.Duration, 3);
		Assert.Contains(mono.Samples, s => s > 15000);   // (half loud)

		var stereo = SoundDecoder.Read(Wav(folder, "b.wav", 0.25, 22050, 2, 24))!;
		Assert.Equal(2, stereo.Channels);
		Assert.Equal(0.25, stereo.Duration, 3);
		var floats = SoundDecoder.Read(Wav(folder, "c.wav", 0.1, 8000, 4, 32))!;
		Assert.Equal(2, floats.Channels);   // (the first two of four)
		Assert.Equal(0.1, floats.Duration, 3);
		Assert.NotNull(SoundDecoder.Read(Wav(folder, "d.wav", 0.1, 8000, 1, 8)));

		File.WriteAllText(Path.Combine(folder, "e.wav"), "not a sound");
		Assert.Null(SoundDecoder.Read(Path.Combine(folder, "e.wav")));
		Assert.Null(SoundDecoder.Read(Path.Combine(folder, "missing.mp3")));
	}

	[Fact]
	public void The_sound_device_opens_or_is_quietly_silent()
	{
		using var output = OpenAlOut.Open();   // (a machine without sound: silent, not an error)
		var sound = SoundDecoder.Read(Wav(Folder(), "a.wav", 0.05))!;
		var voice = output.Start(sound, 0.01f, 1f, 0);
		Assert.Equal(output.Works, voice != null);
		voice?.Stop();
	}

	[AvaloniaFact]
	public void An_event_passed_while_playing_sounds()
	{
		var window = Windows.Show();
		string step = Wav(Folder(), "step.wav", 0.3);
		window.Session.Scene.Sounds["step"] = step;
		Assert.True(window.Session.AddEvent(6, new AnimationEvent("step", "left", 0.5f, 1.5f)));
		window.State.SetFrame(0);
		window.State.Playing = true;
		for (int i = 0; i < 5; i++) window.Tick(0.05f);   // (past frame 6, at 30 fps)
		window.State.Playing = false;

		var heard = Assert.Single(Ears(window).Started);
		Assert.Equal(0.3, heard.Sound.Duration, 3);
		Assert.Equal(0.5f, heard.GainNow);
		Assert.Equal(1.5f, heard.PitchNow);

		// Muted: nothing
		window.SetMuted(true);
		Assert.True(heard.Stopped);
		window.State.SetFrame(0);
		window.State.Playing = true;
		for (int i = 0; i < 5; i++) window.Tick(0.05f);
		window.State.Playing = false;
		Assert.Single(Ears(window).Started);
		window.SetMuted(false);
	}

	[AvaloniaFact]
	public void The_sound_track_keeps_in_step_with_the_playhead()
	{
		var window = Windows.Show();
		string voice = Wav(Folder(), "line.wav", 1.0);
		window.SetSoundTrackFile(voice);
		Assert.NotNull(window.State.Animation.Sound);
		window.Tick(0);
		Assert.NotNull(window.KeysTimeline.Waveform);   // (its waveform under the keys)
		Assert.Empty(Ears(window).Started);             // (not playing: silent)

		window.State.SetFrame(9);   // 0.3 s in
		window.State.Playing = true;
		window.Tick(0);
		var track = Assert.Single(Ears(window).Started);
		Assert.Equal(0.3, track.Position, 2);
		Assert.True(window.Sound.TrackRunning);

		// Drifted: put back where the playhead is
		track.At = 0.9;
		window.Tick(0.1f);
		Assert.Equal(window.State.Time, track.Position, 2);

		window.State.Playing = false;
		window.Tick(0);
		Assert.True(track.Paused);

		// Starting later: silent before it
		window.Session.SetSoundTrack(window.State.Animation.Sound! with { Offset = 1.5f }, "later");
		window.State.SetFrame(0);
		window.State.Playing = true;
		window.Tick(0);
		Assert.False(window.Sound.TrackRunning);
		window.State.Playing = false;
		window.State.Undo.Undo();
		window.State.Undo.Undo();
		Assert.Null(window.State.Animation.Sound);
	}

	[AvaloniaFact]
	public void Events_and_the_sound_map_go_out_and_come_back_in_files()
	{
		var window = Windows.Show();
		string folder = Folder();
		string step = Wav(Path.Combine(folder, "sounds"), "step.wav", 0.2);
		window.Session.AddEvent(3, new AnimationEvent("step"));
		window.Session.AddEvent(12, new AnimationEvent("hit", "big"));
		window.Session.Scene.Sounds["step"] = step;

		string events = Path.Combine(folder, "character.events.json");
		Finish(window.ExportEventsTo(events));
		Assert.Contains("\"hit\"", File.ReadAllText(events));
		string map = Path.Combine(folder, "game.sounds.json");
		Finish(window.ExportSoundMapTo(map));
		Assert.Contains(Path.Combine("sounds", "step.wav").Replace("\\", "\\\\"), File.ReadAllText(map));   // (relative to the map)

		// Gone, then back from the files
		window.State.Animation.Events!.Track.Keys.Clear();
		window.Session.Scene.Sounds.Clear();
		Finish(window.ImportEventsFrom(events, replace: true));
		Assert.Equal(2, window.State.Animation.Events!.Track.Keys.Count);
		Finish(window.ImportSoundMapFrom(map));
		Assert.Equal(Path.GetFullPath(step), Path.GetFullPath(window.Session.Scene.Sounds["step"]));
	}

	[AvaloniaFact]
	public void The_sound_map_lists_the_event_names_and_gives_them_sounds()
	{
		var window = Windows.Show();
		window.Session.AddEvent(4, new AnimationEvent("roar"));
		string roar = Wav(Folder(), "roar.wav", 0.2);
		var map = window.OpenSoundMap(show: false);
		Assert.Contains("roar   (silent)", map.Rows);

		map.AnswerFile = _ => Task.FromResult<string?>(roar);
		map.Pick("roar");
		Finish(map.PickFile());
		Assert.Equal(roar, window.Session.Scene.Sounds["roar"]);
		Assert.Contains(map.Rows, r => r.StartsWith("roar   →"));

		map.Pick("roar");
		map.Remove();
		Assert.False(window.Session.Scene.Sounds.ContainsKey("roar"));
		window.State.Undo.Undo();   // (the removal: undoable)
		Assert.True(window.Session.Scene.Sounds.ContainsKey("roar"));
	}
}
