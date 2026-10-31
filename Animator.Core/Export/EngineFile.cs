using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Core.Export;

// The engine export (name.hqrig.json): one object as a game plays it. The rig and its
// animations exactly as the project keeps them (Serialization/ProjectDto.cs: bones, parts,
// skins, meshes and weights, constraints, masks, hitboxes and points, colour layers, every
// timeline, events, fades), plus where each picture went in the atlas pages, and the sound map.
// What every field means, and how to play it: docs/engine-format.md.
public sealed class EngineFile
{
	public const string FormatName = "harlequin-rig";
	public const int CurrentVersion = 1;

	public string Format { get; set; } = FormatName;
	public int Version { get; set; } = CurrentVersion;
	public string Object { get; set; } = "";
	public int Fps { get; set; } = 30;
	public EngineAtlas Atlas { get; set; } = new();
	public RigDto Rig { get; set; } = new();
	public Dictionary<string, string>? Sounds { get; set; }   // event name → sound file (relative to this file)

	public string ToJson() => JsonSerializer.Serialize(this, ProjectMapper.Json);

	public static EngineFile FromJson(string json)
	{
		try
		{
			var file = JsonSerializer.Deserialize<EngineFile>(json, ProjectMapper.Json) ?? throw new InvalidDataException("The file is empty.");
			if (file.Format != FormatName) throw new InvalidDataException($"Not a Harlequin rig (its format is \"{file.Format}\").");
			return file;
		}
		catch (JsonException ex) { throw new InvalidDataException(ex.Message, ex); }
	}

	// The file for an object: its rig and these animations, the atlas made for its pictures
	public static EngineFile Of(string objectName, Skeleton skeleton, IEnumerable<Animation> animations, int fps, EngineAtlas atlas,
		IReadOnlyDictionary<string, string>? sounds = null) => new()
	{
		Object = objectName,
		Fps = fps,
		Atlas = atlas,
		Rig = ProjectMapper.ToDto(skeleton, animations, fps),
		Sounds = sounds is { Count: > 0 } ? new Dictionary<string, string>(sounds) : null
	};
}

// The atlas: its pages (PNG files beside the rig), and for each picture (by its asset id, as
// the rig's skin entries name it) where it is on them
public sealed class EngineAtlas
{
	public List<EnginePage> Pages { get; set; } = new();
	public Dictionary<Guid, EngineRegion> Regions { get; set; } = new();
}

public sealed class EnginePage
{
	public string File { get; set; } = "";
	public int Width { get; set; }
	public int Height { get; set; }
}

// Where a picture is on a page: X, Y, W, H there. Trimmed of see-through edges: its pixel (px, py)
// is on the page at (X + px - OffsetX, Y + py - OffsetY); Width × Height is its whole size, as its
// drawing's placement, pivot and mesh points count it.
public sealed class EngineRegion
{
	public int Page { get; set; }
	public int X { get; set; }
	public int Y { get; set; }
	public int W { get; set; }
	public int H { get; set; }
	public int OffsetX { get; set; }
	public int OffsetY { get; set; }
	public int Width { get; set; }
	public int Height { get; set; }
}
