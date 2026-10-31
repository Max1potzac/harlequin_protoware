using System.Numerics;
using System.Text.RegularExpressions;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// A pose kept by name (the Poses view): how some bones and parts were posed, to put them that
// way again in any animation, at any frame, mirrored or not, fully or partly
public sealed class SavedPose
{
	public string Name { get; set; } = "Pose";
	public Dictionary<string, (Vector2 Position, float Rotation, Vector2 Scale, float Turn, float Tilt)> Bones { get; } = new();
	public Dictionary<string, (Vector2 Offset, float Rotation, Vector2 Scale)> Parts { get; } = new();

	public SavedPose Copy(string name)
	{
		var copy = new SavedPose { Name = name };
		foreach (var (bone, pose) in Bones) copy.Bones[bone] = pose;
		foreach (var (part, pose) in Parts) copy.Parts[part] = pose;
		return copy;
	}
}

// Left and right: names that say a side (arm_L, Left Arm, hand.r, legRight…), and the other side's
public static class MirrorNames
{
	// A side word between separators (or at either end), or a capitalised Left / Right in a camelCase name
	private static readonly Regex Sided = new(
		@"(?<=^|[_.\-\s])(?<side>L|R|l|r|Left|Right|left|right|LEFT|RIGHT)(?=$|[_.\-\s])" +
		@"|(?<=[a-z0-9])(?<side>Left|Right|L|R)(?=$|[A-Z0-9_.\-\s])" +
		@"|^(?<side>Left|Right)(?=[A-Z0-9])",
		RegexOptions.CultureInvariant);

	// The other side's name (null: it doesn't say a side)
	public static string? Other(string name)
	{
		var match = Sided.Match(name);
		if (!match.Success) return null;
		var side = match.Groups["side"];
		string swapped = side.Value switch
		{
			"L" => "R", "R" => "L", "l" => "r", "r" => "l",
			"Left" => "Right", "Right" => "Left", "left" => "right", "right" => "left",
			"LEFT" => "RIGHT", _ => "LEFT"
		};
		return name[..side.Index] + swapped + name[(side.Index + side.Length)..];
	}
}

public static class PoseOps
{
	// The pose of these bones and parts now (null: all of them)
	public static SavedPose Capture(Skeleton skeleton, string name, IEnumerable<Bone>? bones = null, IEnumerable<Slot>? parts = null)
	{
		var pose = new SavedPose { Name = name };
		foreach (var b in bones ?? skeleton.AllBones())
			pose.Bones[b.Name] = (b.Position, b.Rotation, b.Scale, b.Turn, b.Tilt);
		foreach (var s in parts ?? (bones == null ? skeleton.Slots : Enumerable.Empty<Slot>()))
			pose.Parts[s.Name] = (s.Offset, s.Rotation, s.Scale);
		return pose;
	}

	// The rig posed that way (amount 0..1: partway from how it is), on the bones and parts the pose
	// has (only: just these of them). The pose itself, not keys: see Keyed for those.
	public static void Apply(Skeleton skeleton, SavedPose pose, float amount = 1f, IReadOnlyCollection<object>? only = null)
	{
		float t = Math.Clamp(amount, 0f, 1f);
		foreach (var (name, p) in pose.Bones)
		{
			if (skeleton.FindBone(name) is not { } b || (only != null && !only.Contains(b))) continue;
			b.Position = Vector2.Lerp(b.Position, p.Position, t);
			b.Rotation += MathUtil.ShortestAngle(p.Rotation - b.Rotation) * t;
			b.Scale = Vector2.Lerp(b.Scale, p.Scale, t);
			b.Turn += (p.Turn - b.Turn) * t;
			b.Tilt += (p.Tilt - b.Tilt) * t;
		}
		foreach (var (name, p) in pose.Parts)
		{
			if (skeleton.FindSlot(name) is not { } s || (only != null && !only.Contains(s))) continue;
			s.Offset = Vector2.Lerp(s.Offset, p.Offset, t);
			s.Rotation += MathUtil.ShortestAngle(p.Rotation - s.Rotation) * t;
			s.Scale = Vector2.Lerp(s.Scale, p.Scale, t);
		}
		skeleton.UpdateWorld();
	}

	// The pose with its sides swapped, as the rig is now: each bone takes its other side's pose,
	// flipped left to right in the object (a bone with no other side flips itself). Exact for any
	// rig: worked out where the bones end up in the object, then back to how each hangs from its
	// parent. Parts swap their placements with the other side's.
	public static void Mirror(Skeleton skeleton, IEnumerable<Bone>? bones = null, IEnumerable<Slot>? parts = null)
	{
		skeleton.UpdateWorld();
		var chosen = (bones ?? skeleton.AllBones()).ToHashSet();
		// Where every bone is now, before any of them changes
		var now = skeleton.AllBones().ToDictionary(b => b, b => (Head: b.World.Translation, Angle: b.WorldRotation, b.Scale, b.Turn, b.Tilt));
		foreach (var bone in skeleton.AllBones())   // parents first
		{
			if (!chosen.Contains(bone)) continue;
			var source = MirrorNames.Other(bone.Name) is { } other && skeleton.FindBone(other) is { } found ? found : bone;
			var from = now[source];
			var head = new Vector2(-from.Head.X, from.Head.Y);
			float angle = MathF.PI - from.Angle;
			bone.Position = bone.WorldToParentLocal(head);
			bone.Rotation += MathUtil.ShortestAngle(angle - ParentAngle(bone) - bone.Rotation);   // (the nearest way round: keys blend smoothly)
			bone.Scale = from.Scale;
			bone.Turn = -from.Turn;
			bone.Tilt = from.Tilt;
			bone.UpdateWorld();
		}

		var swapped = new HashSet<Slot>();
		foreach (var slot in (parts ?? skeleton.Slots).ToList())
		{
			if (swapped.Contains(slot) || MirrorNames.Other(slot.Name) is not { } other || skeleton.FindSlot(other) is not { } pair) continue;
			(slot.Offset, pair.Offset) = (pair.Offset, slot.Offset);
			(slot.Rotation, pair.Rotation) = (pair.Rotation, slot.Rotation);
			(slot.Scale, pair.Scale) = (pair.Scale, slot.Scale);
			swapped.Add(slot);
			swapped.Add(pair);
		}
	}

	// A pose put on the other side: the bones and parts on the other side of the ones it has (and
	// the ones with no side, flipped) take it, mirrored; the rest stay as they are
	public static void ApplyMirrored(Skeleton skeleton, SavedPose pose, float amount = 1f)
	{
		var asItWas = Capture(skeleton, "");
		Apply(skeleton, pose);
		var bones = pose.Bones.Keys.Select(n => skeleton.FindBone(MirrorNames.Other(n) ?? n)).OfType<Bone>().ToList();
		var parts = pose.Parts.Keys.SelectMany(n => new[] { skeleton.FindSlot(n), skeleton.FindSlot(MirrorNames.Other(n) ?? n) }).OfType<Slot>().Distinct().ToList();
		Mirror(skeleton, bones, parts);
		var mirrored = Capture(skeleton, "", bones, Enumerable.Empty<Slot>());
		foreach (var name in pose.Parts.Keys)
			if (skeleton.FindSlot(MirrorNames.Other(name) ?? name) is { } target) mirrored.Parts[target.Name] = (target.Offset, target.Rotation, target.Scale);
		Restore(skeleton, asItWas);
		Apply(skeleton, mirrored, amount);
	}

	// Exactly as captured (not blended: angles come back as they were, not just pointing the same way)
	private static void Restore(Skeleton skeleton, SavedPose pose)
	{
		foreach (var (name, p) in pose.Bones)
			if (skeleton.FindBone(name) is { } b) (b.Position, b.Rotation, b.Scale, b.Turn, b.Tilt) = p;
		foreach (var (name, p) in pose.Parts)
			if (skeleton.FindSlot(name) is { } s) (s.Offset, s.Rotation, s.Scale) = p;
		skeleton.UpdateWorld();
	}

	// The angle a bone's own turn is counted from: its parent's (when it follows it)
	private static float ParentAngle(Bone bone) =>
		bone.SpaceParent is { } parent && bone.InheritRotation ? parent.WorldRotation : 0f;

	// Symmetrize (Setup mode): the other side of these bones made the mirror image of them at rest:
	// moved there if it's there already, made if it isn't (hanging from the mirror of their parent,
	// or the same parent). Only bones whose names say a side. The bones it made.
	public static List<Bone> Symmetrize(Skeleton skeleton, IEnumerable<Bone> bones)
	{
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		var made = new List<Bone>();
		var chosen = bones.ToHashSet();
		var rest = skeleton.AllBones().ToDictionary(b => b, b => (Head: b.World.Translation, Angle: b.WorldRotation));
		foreach (var bone in skeleton.AllBones().ToList())   // parents first, so a made one's parent is made already
		{
			if (!chosen.Contains(bone) || MirrorNames.Other(bone.Name) is not { } otherName) continue;
			var other = skeleton.FindBone(otherName);
			if (other == null)
			{
				var parent = bone.Parent is { } p && MirrorNames.Other(p.Name) is { } pn && skeleton.FindBone(pn) is { } mirroredParent
					? mirroredParent : bone.Parent;
				other = new Bone
				{
					Name = otherName,
					Length = bone.Length,
					InheritRotation = bone.InheritRotation,
					InheritScale = bone.InheritScale,
					SetupScale = bone.SetupScale,
					EditorColor = bone.EditorColor
				};
				if (parent == null) skeleton.Roots.Add(other); else parent.AddChild(other);
				made.Add(other);
			}
			else if (other.IsDescendantOf(bone)) continue;   // (the other side hangs from this one: leave it)
			var from = rest[bone];
			other.Position = other.WorldToParentLocal(new Vector2(-from.Head.X, from.Head.Y));
			other.Rotation = MathF.PI - from.Angle - ParentAngle(other);
			other.SetupPosition = other.Position;
			other.SetupRotation = MathUtil.ShortestAngle(other.Rotation);
			other.SetupScale = bone.SetupScale;
			other.Length = bone.Length;
			other.UpdateWorld();
			rest[other] = (other.World.Translation, other.WorldRotation);
		}
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		return made;
	}
}
