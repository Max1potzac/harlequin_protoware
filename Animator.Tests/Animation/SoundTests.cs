using System.IO;
using System.Text.Json;
using Animator.Core.Export;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Step 25: sound events (volume, pitch), sound tracks, the sound map, and the events and sound
// map files that carry them to and from the game and other tools
public class SoundTests
{
	private static Animation WithEvents(string name, float duration, params (float Time, AnimationEvent Event)[] events)
	{
		var a = new Animation { Name = name, Duration = duration };
		foreach (var (time, e) in events) a.GetOrCreateEvents().Add(time, e);
		return a;
	}

	[Fact]
	public void The_events_file_carries_events_and_sound_tracks_out_and_back_in()
	{
		var walk = WithEvents("walk", 1f, (0.2f, new AnimationEvent("footstep", Volume: 0.5f)), (0.7f, new AnimationEvent("footstep", "left", Pitch: 1.2f)));
		walk.Sound = new SoundTrack("voice/groan.wav", -0.1f, 0.8f);
		var idle = new Animation { Name = "idle", Duration = 1f };   // nothing to say: left out
		var file = EventsFile.Of("zombie", new[] { walk, idle }, 30);
		var json = file.ToJson();
		Assert.Contains("\"footstep\"", json);

		var read = EventsFile.FromJson(json);
		var animation = Assert.Single(read.Animations);
		Assert.Equal(("zombie", 30, "walk"), (read.Object, read.Fps, animation.Name));
		Assert.Equal(new int?[] { 6, 21 }, animation.Events.Select(e => e.Frame));
		Assert.Equal("left", animation.Events[1].Data);
		Assert.Equal((0.5f, 1.2f), (animation.Events[0].Volume!.Value, animation.Events[1].Pitch!.Value));

		var target = new Animation { Name = "WALK", Duration = 1f };   // (names match whatever the case)
		var merged = read.MergeInto(new[] { target }, 30, replace: true);
		Assert.Equal((2, 0, 1), (merged.Added, merged.Replaced, merged.SoundTracks));
		var keys = target.Events!.Track.Keys;
		Assert.Equal(new[] { 0.2f, 0.7f }, keys.Select(k => MathF.Round(k.Time, 4)));
		Assert.Equal(new AnimationEvent("footstep", "left", 1f, 1.2f), keys[1].Value);
		Assert.Equal(new SoundTrack("voice/groan.wav", -0.1f, 0.8f), target.Sound);
	}

	[Fact]
	public void A_hand_written_events_file_may_give_times_or_frames_at_its_own_fps()
	{
		const string json = """
			{
			  // from a tool: frames at 24 fps, or seconds
			  "fps": 24,
			  "animations": [
			    { "name": "attack", "events": [ { "frame": 12, "name": "PlaySample", "data": "swing" }, { "time": 0.25, "name": "Play_Hit" } ] },
			    { "name": "dance", "events": [ { "frame": 1, "name": "x" } ] },
			  ]
			}
			""";
		var attack = WithEvents("attack", 1f, (0.5f, new AnimationEvent("old")), (0.9f, new AnimationEvent("kept")));
		var merged = EventsFile.FromJson(json).MergeInto(new[] { attack }, 30, replace: false);
		Assert.Equal(new[] { "dance" }, merged.Missing);
		Assert.Equal((1, 1), (merged.Added, merged.Replaced));   // frame 12 at 24 fps = 0.5 s: the old one there is replaced
		var keys = attack.Events!.Track.Keys.OrderBy(k => k.Time).ToList();
		Assert.Equal(new[] { "Play_Hit", "PlaySample", "kept" }, keys.Select(k => k.Value.Name));
		Assert.Equal("swing", keys[1].Value.Data);
		Assert.Equal(8, (int)MathF.Round(keys[0].Time * 30));   // 0.25 s, onto the project's frame 8 (7.5 rounded)

		Assert.Throws<InvalidDataException>(() => EventsFile.FromJson("{ not json"));
	}

	[Fact]
	public void Events_sound_tracks_and_the_sound_map_are_saved_and_undone()
	{
		var walk = WithEvents("walk", 1f, (0.5f, new AnimationEvent("step", null, 0.25f, 0.9f)));
		walk.Sound = new SoundTrack("music.wav", 0.5f, 0.6f);
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "root" });
		var obj = new SceneObject("hero", ObjectKind.Rigged, skeleton);
		obj.Animations.Add(walk);
		var scene = new Scene();
		scene.Objects.Add(obj);
		scene.Sounds["step"] = "sfx/step.wav";

		var loaded = ProjectMapper.SceneFromDto(JsonSerializer.Deserialize<ProjectDto>(
			JsonSerializer.Serialize(ProjectMapper.SceneToDto(scene), ProjectMapper.Json), ProjectMapper.Json)!);
		var a = loaded.Objects.Single().Animations.Single();
		Assert.Equal(new AnimationEvent("step", null, 0.25f, 0.9f), a.Events!.Track.Keys.Single().Value);
		Assert.Equal(walk.Sound, a.Sound);
		Assert.Equal("sfx/step.wav", loaded.Sounds["step"]);
		Assert.Equal(walk.Sound, ProjectMapper.CloneAnimation(walk, "walk 2").Sound);

		var keys = AnimationSnapshot.Capture(walk);
		var map = SceneSnapshot.Capture(scene);
		walk.Sound = null;
		scene.Sounds.Clear();
		keys.Restore();
		map.Restore();
		Assert.NotNull(walk.Sound);
		Assert.Equal("sfx/step.wav", scene.Sounds["step"]);

		var sounds = SoundMapFile.FromJson(new SoundMapFile { Sounds = { ["step"] = "a.wav" } }.ToJson());
		Assert.Equal("a.wav", sounds.Sounds["step"]);
	}

	// A one-second 16-bit mono WAV at 1000 samples a second: silent, then loud halfway
	private static MemoryStream Wav()
	{
		var stream = new MemoryStream();
		var w = new BinaryWriter(stream);
		w.Write("RIFF"u8.ToArray()); w.Write(36 + 2000); w.Write("WAVE"u8.ToArray());
		w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(1000); w.Write(2000); w.Write((short)2); w.Write((short)16);
		w.Write("data"u8.ToArray()); w.Write(2000);
		for (int i = 0; i < 1000; i++) w.Write((short)(i < 500 ? 0 : (i % 2 == 0 ? 30000 : -30000)));
		w.Flush();
		stream.Position = 0;
		return stream;
	}

	[Fact]
	public void A_wavs_loudness_over_time_is_read_for_the_waveform()
	{
		using var stream = Wav();
		var (duration, peaks) = WavPeaks.Read(stream, 10)!.Value;
		Assert.Equal(1f, duration, 3);
		Assert.Equal(10, peaks.Length);
		Assert.All(peaks.Take(5), p => Assert.Equal(0f, p));
		Assert.All(peaks.Skip(5), p => Assert.Equal(30000 / 32768f, p, 3));

		using var notWav = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
		Assert.Null(WavPeaks.Read(notWav, 10));
	}
}
