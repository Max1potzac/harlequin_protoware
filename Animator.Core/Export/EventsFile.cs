using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animator.Core.Export;

// The events file (name.events.json): an object's animations' events, plain, for the game and
// for tools that make or read them. Written on export (and by Export events…); read by Import
// events…, which merges it into the animations of the same names. The schema:
//
//   {
//     "object": "zombie", "fps": 30,
//     "animations": [
//       { "name": "walk",
//         "events": [ { "frame": 6, "time": 0.2, "name": "footstep", "data": null, "volume": 1, "pitch": 1 } ],
//         "sound": { "path": "voice/groan.wav", "offset": 0, "volume": 1 } }
//     ]
//   }
//
// When reading, each event needs "name" and either "frame" or "time" (seconds); the rest may be
// left out. A frame counts at the file's fps (or the project's, when the file has none).
public sealed class EventsFile
{
	public string? Object { get; set; }
	public int? Fps { get; set; }
	public List<EventsFileAnimation> Animations { get; set; } = new();

	public static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	public string ToJson() => JsonSerializer.Serialize(this, Options);

	// (anything wrong with it: InvalidDataException, saying what)
	public static EventsFile FromJson(string json)
	{
		try { return JsonSerializer.Deserialize<EventsFile>(json, Options) ?? throw new InvalidDataException("The events file is empty."); }
		catch (JsonException ex) { throw new InvalidDataException(ex.Message, ex); }
	}

	// An object's animations' events (and sound tracks), frames at this fps
	public static EventsFile Of(string objectName, IEnumerable<Animation> animations, int fps)
	{
		var file = new EventsFile { Object = objectName, Fps = fps };
		foreach (var animation in animations)
		{
			var events = animation.Events?.Track.Keys ?? new List<Keyframe<AnimationEvent>>();
			if (events.Count == 0 && animation.Sound == null) continue;
			file.Animations.Add(new EventsFileAnimation
			{
				Name = animation.Name,
				Events = events.Select(k => new EventsFileEvent
				{
					Frame = (int)MathF.Round(k.Time * fps),
					Time = MathF.Round(k.Time, 4),
					Name = k.Value.Name,
					Data = k.Value.Data,
					Volume = k.Value.Volume,
					Pitch = k.Value.Pitch
				}).ToList(),
				Sound = animation.Sound is { } s ? new EventsFileSound { Path = s.Path, Offset = s.Offset, Volume = s.Volume } : null
			});
		}
		return file;
	}

	// What merging would do, or did: events added, replaced, and animations it names that aren't there
	public sealed record Merged(int Added, int Replaced, List<string> Missing, int SoundTracks);

	// Its events into the animations of the same names. replace: each one's events go first (else
	// they're added, one on a frame that has one replacing it). sounds: their sound tracks too.
	public Merged MergeInto(IReadOnlyList<Animation> animations, int projectFps, bool replace, bool sounds = true)
	{
		int fps = Fps is > 0 ? Fps.Value : projectFps;
		int added = 0, replaced = 0, tracks = 0;
		var missing = new List<string>();
		foreach (var a in Animations)
		{
			var animation = animations.FirstOrDefault(x => x.Name == a.Name)
				?? animations.FirstOrDefault(x => string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase));
			if (animation == null)
			{
				missing.Add(a.Name);
				continue;
			}
			var events = animation.GetOrCreateEvents();
			if (replace)
			{
				replaced += events.Track.Keys.Count;
				events.Track.Keys.Clear();
			}
			foreach (var e in a.Events.Where(e => !string.IsNullOrWhiteSpace(e.Name)))
			{
				float time = e.Frame is { } frame ? frame / (float)fps : e.Time ?? 0f;
				time = MathF.Round(time * projectFps) / projectFps;   // onto a frame of the project
				if (events.Track.Keys.Any(k => MathF.Abs(k.Time - time) < KeyHelpers.Epsilon)) replaced++;
				else added++;
				events.Add(time, new AnimationEvent(e.Name.Trim(), string.IsNullOrEmpty(e.Data) ? null : e.Data, e.Volume ?? 1f, e.Pitch ?? 1f));
			}
			if (sounds && a.Sound is { Path.Length: > 0 } s)
			{
				animation.Sound = new SoundTrack(s.Path, s.Offset, s.Volume ?? 1f);
				tracks++;
			}
		}
		return new Merged(added, replaced, missing, tracks);
	}
}

public sealed class EventsFileAnimation
{
	public string Name { get; set; } = "";
	public List<EventsFileEvent> Events { get; set; } = new();
	public EventsFileSound? Sound { get; set; }
}

public sealed class EventsFileEvent
{
	public int? Frame { get; set; }
	public float? Time { get; set; }
	public string Name { get; set; } = "";
	public string? Data { get; set; }
	public float? Volume { get; set; }
	public float? Pitch { get; set; }
}

public sealed class EventsFileSound
{
	public string Path { get; set; } = "";
	public float Offset { get; set; }
	public float? Volume { get; set; }
}

// The sound map file (name.sounds.json): what each event name sounds like, a sound file's path
// (relative to the file itself, or full).  { "sounds": { "footstep": "sfx/step.wav" } }
public sealed class SoundMapFile
{
	public Dictionary<string, string> Sounds { get; set; } = new();

	public string ToJson() => JsonSerializer.Serialize(this, EventsFile.Options);

	public static SoundMapFile FromJson(string json)
	{
		try { return JsonSerializer.Deserialize<SoundMapFile>(json, EventsFile.Options) ?? new SoundMapFile(); }
		catch (JsonException ex) { throw new InvalidDataException(ex.Message, ex); }
	}
}
