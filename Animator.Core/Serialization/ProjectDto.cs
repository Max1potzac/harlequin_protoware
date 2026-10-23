using System.Numerics;

namespace Animator.Core.Serialization;

// Plain data shaped for JSON. These mirror the model but are kept separate from it,
// so the file format can stay stable while the program's classes evolve.

public record Vec(float X, float Y)
{
	public static Vec From(Vector2 v) => new(v.X, v.Y);
	public Vector2 ToVector2() => new(X, Y);
}

public class ProjectDto
{
	public int Fps { get; set; } = 30;
	public List<BoneDto> Bones { get; set; } = new();
	public List<SlotDto> Slots { get; set; } = new();
	public List<AssetDto> Assets { get; set; } = new();
	public List<SkinDto> Skins { get; set; } = new();
	public string? ActiveSkin { get; set; }
	public List<IkDto> IkConstraints { get; set; } = new();
	public List<LookDto> LookConstraints { get; set; } = new();
	public List<AnimationDto> Animations { get; set; } = new();
}

public class BoneDto
{
	public string Name { get; set; } = "";
	public string? Parent { get; set; }            // null = root bone
	public float Length { get; set; }
	public Vec Position { get; set; } = new(0, 0);
	public float Rotation { get; set; }            // radians
	public Vec Scale { get; set; } = new(1, 1);
	public bool InheritRotation { get; set; } = true;
	public bool InheritScale { get; set; } = true;
	public Vec? MinOffset { get; set; }
	public Vec? MaxOffset { get; set; }
	public float Turn { get; set; }   // depth, radians
	public float Tilt { get; set; }
}

public class SlotDto
{
	public string Name { get; set; } = "";
	public string Bone { get; set; } = "";
	public string? Attachment { get; set; }
}

public class AssetDto
{
	public Guid Id { get; set; }
	public string Name { get; set; } = "";
	public int Width { get; set; }
	public int Height { get; set; }
	public List<LayerDto> Layers { get; set; } = new();   // pixels are in layers/<id>.png
}

public class LayerDto
{
	public Guid Id { get; set; }
	public string Name { get; set; } = "";
	public bool Visible { get; set; } = true;
	public float Opacity { get; set; } = 1f;
}

public class SkinDto
{
	public string Name { get; set; } = "";
	public List<SkinEntryDto> Entries { get; set; } = new();
}

public class SkinEntryDto
{
	public string Slot { get; set; } = "";
	public string Name { get; set; } = "";
	public Guid Asset { get; set; }
	public Vec Offset { get; set; } = new(0, 0);
	public float Rotation { get; set; }
	public Vec Scale { get; set; } = new(1, 1);
	public Vec Pivot { get; set; } = new(0.5f, 0.5f);
}

public class IkDto
{
	public string Name { get; set; } = "";
	public List<string> Bones { get; set; } = new();
	public string Target { get; set; } = "";
	public float Mix { get; set; } = 1f;
	public int Bend { get; set; } = 1;
	public int Iterations { get; set; } = 15;
}

public class AnimationDto
{
	public string Name { get; set; } = "";
	public float Duration { get; set; }
	public bool Loop { get; set; }
	public List<TimelineDto> Timelines { get; set; } = new();
}

public class TimelineDto
{
	public string Type { get; set; } = "";    // rotate, translate, scale, attachment, ikMix
	public string Target { get; set; } = "";
	public List<KeyDto> Keys { get; set; } = new();
}

// One shape for every key type: rotate/ikMix use X, translate/scale use X+Y,
// attachment uses Name (null = hide the slot)
public class KeyDto
{
	public float T { get; set; }
	public CurveType Curve { get; set; }
	public float? X { get; set; }
	public float? Y { get; set; }
	public string? Name { get; set; }
}
public class LookDto
{
	public string Name { get; set; } = "";
	public string Bone { get; set; } = "";
	public string Target { get; set; } = "";
	public LookShape Shape { get; set; }
	public Vec Radius { get; set; } = new(10, 6);
	public float Follow { get; set; } = 0.25f;
	public float Mix { get; set; } = 1f;
}