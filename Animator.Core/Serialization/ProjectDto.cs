using System.Numerics;

namespace Animator.Core.Serialization;

// Plain data shaped for JSON. These mirror the model but are kept separate from it,
// so the file format can stay stable while the program's classes evolve.

public record Vec(float X, float Y)
{
	public static Vec From(Vector2 v) => new(v.X, v.Y);
	public Vector2 ToVector2() => new(X, Y);
}

// The whole file. Since format 6 a project is a scene: a list of objects, each with its own
// rig and animations. Older files kept a single rig at the top level (the RigDto fields),
// and still load: as one object.
public class ProjectDto : RigDto
{
	public int Fps { get; set; } = 30;
	public List<ObjectDto>? Objects { get; set; }

	// Since format 7: what the project is for, and the scene timeline (null = the defaults)
	public ProjectKind? Kind { get; set; }
	public float? SceneDuration { get; set; }
	public bool? SceneLoop { get; set; }
	public int? StageWidth { get; set; }
	public int? StageHeight { get; set; }

	// Editing aids: the grid's spacing and the guides (null = none: a 10 grid, no guides)
	public float? GridSize { get; set; }
	public List<GuideDto>? Guides { get; set; }
}

public class GuideDto
{
	public bool Vertical { get; set; }   // up and down, at this x; otherwise across, at this y
	public float At { get; set; }
}

// One object in the scene: its place and settings, plus its rig and animations
public class ObjectDto : RigDto
{
	public string Name { get; set; } = "";
	public ObjectKind Kind { get; set; }
	public Vec Position { get; set; } = new(0, 0);
	public float Rotation { get; set; }
	public Vec Scale { get; set; } = new(1, 1);
	public bool Visible { get; set; } = true;
	public bool Locked { get; set; }
	public bool Export { get; set; } = true;
	public bool Onion { get; set; } = true;
	public string? PreviewAnimation { get; set; }
	public string? AttachObject { get; set; }   // attached to this object's bone (null = free)
	public string? AttachBone { get; set; }
	public List<ClipDto>? Clips { get; set; }   // on the scene timeline (format 7)
}

// A clip of one of the object's animations on the scene timeline, in seconds
public class ClipDto
{
	public string Animation { get; set; } = "";
	public float Start { get; set; }
	public float Length { get; set; }
	public float Offset { get; set; }
	public bool Loop { get; set; }
}

// A rig: bones, parts, pixels, skins, constraints and its animations
public class RigDto
{
	public List<BoneDto> Bones { get; set; } = new();
	public List<SlotDto> Slots { get; set; } = new();
	public List<AssetDto> Assets { get; set; } = new();
	public List<SkinDto> Skins { get; set; } = new();
	public string? ActiveSkin { get; set; }                 // (before format 11: the one skin on)
	public List<string>? ShownSkins { get; set; }           // the skins on (format 11)
	public List<IkDto> IkConstraints { get; set; } = new();
	public List<LookDto> LookConstraints { get; set; } = new();
	public List<SetDto>? Sets { get; set; }   // selection sets (null = none)
	public List<ColorLayerDto>? ColorLayers { get; set; }   // colour layers on the whole object (format 9)
	public List<PoseDto>? Poses { get; set; }               // poses kept by name (format 10)
	public List<HitboxDto>? Hitboxes { get; set; }          // hitboxes on bones (format 12)
	public List<PointDto>? Points { get; set; }             // points on bones (format 12)
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
	public bool? Connected { get; set; }   // snapped to the parent's end (null = no)
	public bool? Hidden { get; set; }      // hidden while editing (null = no)
	public bool? Locked { get; set; }      // can't be picked in the viewport (null = no)
	public Rgb? EditorColor { get; set; }  // its line's colour while editing (null = the usual)
}

public class SlotDto
{
	public string Name { get; set; } = "";
	public string Bone { get; set; } = "";
	public string? Attachment { get; set; }
	public BlendMode? Blend { get; set; }    // null = Normal
	public Rgb? Color { get; set; }          // null = white (drawn as is)
	public float? Opacity { get; set; }      // null = 1
	public bool? Hidden { get; set; }        // hidden while editing (null = no)
	public bool? Locked { get; set; }        // can't be picked in the viewport (null = no)
	public MaskDto? Mask { get; set; }       // a clipping mask (format 13; null = not one)
}

// A part used as a clipping mask: the last part it clips (null = all above it), and whether it shows
public class MaskDto
{
	public string? Until { get; set; }
	public bool? Shows { get; set; }
}

// A selection set: its name, and the names of its bones and parts
public class SetDto
{
	public string Name { get; set; } = "";
	public List<string>? Bones { get; set; }
	public List<string>? Parts { get; set; }
}

// A colour as red, green, blue, 0..1 each
public record Rgb(float R, float G, float B)
{
	public static Rgb From(Vector3 v) => new(v.X, v.Y, v.Z);
	public Vector3 ToVector3() => new(R, G, B);
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
	public AdjustDto? Adjust { get; set; }   // a colour layer, not pixels (format 9)
}

// What a colour layer does: its kind, switches and numbers at rest (see AdjustValues.ToArray)
public class AdjustDto
{
	public ColorAdjustKind Kind { get; set; }
	public bool? Colorize { get; set; }
	public bool? KeepLight { get; set; }
	public float[]? Values { get; set; }
}

// A hitbox on a bone: a box (width × height) or a circle (diameter), its kind, whether it's on at rest
public class HitboxDto
{
	public string Name { get; set; } = "";
	public string Bone { get; set; } = "";
	public string Shape { get; set; } = "box";   // box, circle
	public Vec Offset { get; set; } = new(0, 0);
	public float Rotation { get; set; }
	public Vec Size { get; set; } = new(40, 40);
	public string Kind { get; set; } = "hurt";
	public bool Active { get; set; } = true;
}

// A point on a bone, and which way it faces
public class PointDto
{
	public string Name { get; set; } = "";
	public string Bone { get; set; } = "";
	public Vec Offset { get; set; } = new(0, 0);
	public float Rotation { get; set; }
}

// A pose kept by name: each bone's place, angle, scale and depth, each part's placement
public class PoseDto
{
	public string Name { get; set; } = "";
	public List<PoseBoneDto> Bones { get; set; } = new();
	public List<PosePartDto>? Parts { get; set; }
}

public class PoseBoneDto
{
	public string Name { get; set; } = "";
	public Vec Position { get; set; } = new(0, 0);
	public float Rotation { get; set; }
	public Vec Scale { get; set; } = new(1, 1);
	public float? Turn { get; set; }
	public float? Tilt { get; set; }
}

public class PosePartDto
{
	public string Name { get; set; } = "";
	public Vec Offset { get; set; } = new(0, 0);
	public float Rotation { get; set; }
	public Vec Scale { get; set; } = new(1, 1);
}

public class ColorLayerDto
{
	public string Name { get; set; } = "";
	public bool Visible { get; set; } = true;
	public AdjustDto Adjust { get; set; } = new();
	public List<string>? LeaveOut { get; set; }   // the parts it doesn't change
}

public class SkinDto
{
	public string Name { get; set; } = "";
	public string? Category { get; set; }   // (format 11)
	public bool Overlay { get; set; }
	public List<string>? ColorLayers { get; set; }   // the object's colour layers in it, by name
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
	public string Type { get; set; } = "";    // rotate, translate, scale, depth, attachment, ikMix, space, events,
	                                          // partTranslate, partRotate, partScale, partColor, partOpacity, drawOrder
	public string Target { get; set; } = "";
	public string? Caption { get; set; }   // layerAdjust: what its row says
	public List<KeyDto> Keys { get; set; } = new();
}

// One shape for every key type: rotate/ikMix/partRotate/partOpacity use X,
// translate/scale/depth/partTranslate/partScale use X+Y, partColor uses X+Y+Z (red, green, blue),
// attachment uses Name (null = hide the slot), space uses Name (null = own parent,
// "" = detached, otherwise the holding bone), events use Name + Data, drawOrder uses Order (the
// parts' names, back to front; since format 8)
public class KeyDto
{
	public float T { get; set; }
	public CurveType Curve { get; set; }
	public float? X { get; set; }
	public float? Y { get; set; }
	public float? Z { get; set; }
	public string? Name { get; set; }
	public string? Data { get; set; }
	public List<string>? Order { get; set; }
	public float[]? V { get; set; }   // colorLayer, layerAdjust: a colour layer's numbers (format 9)
	public float[]? B { get; set; }   // a Bezier curve's handles: x1, y1, x2, y2 (format 10)
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