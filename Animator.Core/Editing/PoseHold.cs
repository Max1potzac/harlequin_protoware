using System.Numerics;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// How bones and parts are posed at one moment: the values keys set (a bone's place, angle,
// scale and depth turn/tilt; a part's placement on its bone)
public sealed class PoseValues
{
	internal readonly Dictionary<Bone, BonePose> Bones = new();
	internal readonly Dictionary<Slot, PartPose> Parts = new();

	public static PoseValues Capture(Skeleton skeleton)
	{
		var pose = new PoseValues();
		foreach (var bone in skeleton.AllBones())
			pose.Bones[bone] = new BonePose(bone.Position, bone.Rotation, bone.Scale, bone.Turn, bone.Tilt);
		foreach (var slot in skeleton.Slots)
			pose.Parts[slot] = new PartPose(slot.Offset, slot.Rotation, slot.Scale);
		return pose;
	}
}

internal readonly record struct BonePose(Vector2 Position, float Rotation, Vector2 Scale, float Turn, float Tilt);
internal readonly record struct PartPose(Vector2 Offset, float Rotation, Vector2 Scale);

// What a key would hold, one channel each
[Flags]
public enum PoseChannels
{
	None = 0,
	Move = 1,      // a bone's place, or a part's
	Turn = 2,      // a bone's angle, or a part's
	Scale = 4,
	Depth = 8,     // a bone's turn and tilt (the depth keys)
	All = Move | Turn | Scale | Depth
}

// Auto key off: posing in Animate mode changes the pose without setting keys. The changes are held
// here, on top of what the keys say, for this frame of this animation only: keying them (S) turns
// them into keys, and going to another frame (or playing, or another animation) lets them go.
public sealed class PoseHold
{
	private readonly Dictionary<Bone, (BonePose Pose, PoseChannels Channels)> _bones = new();
	private readonly Dictionary<Slot, (PartPose Pose, PoseChannels Channels)> _parts = new();
	private readonly Dictionary<ColorLayer, AdjustValues> _colors = new();                      // an object's colour layers
	private readonly Dictionary<AssetLayer, (Asset Asset, AdjustValues Values)> _layerColors = new();   // colour layers in drawings

	public Skeleton? Skeleton { get; private set; }
	public Animation? Animation { get; private set; }
	public int Frame { get; private set; }

	// While a drag is going on (its keys stand in for the held values meanwhile): nothing applied
	public bool Suspended { get; set; }

	public bool Any => Count > 0;
	public int Count => _bones.Count + _parts.Count + _colors.Count + _layerColors.Count;
	public IEnumerable<Bone> HeldBones => _bones.Keys;
	public IEnumerable<Slot> HeldParts => _parts.Keys;
	public bool Holds(object item) => item switch
	{
		Bone b => _bones.ContainsKey(b),
		Slot s => _parts.ContainsKey(s),
		ColorLayer c => _colors.ContainsKey(c),
		AssetLayer l => _layerColors.ContainsKey(l),
		_ => false
	};

	public void Clear()
	{
		_bones.Clear();
		_parts.Clear();
		_colors.Clear();
		_layerColors.Clear();
		Skeleton = null;
		Animation = null;
		Suspended = false;
	}

	// What changed from `before` to `after` is held (on top of anything held already, if it's the
	// same frame of the same animation; otherwise that goes). true: something is held now.
	public bool Take(PoseValues before, PoseValues after, Skeleton skeleton, Animation animation, int frame)
	{
		Start(skeleton, animation, frame);

		// Bones an IK chain turns: their angle is IK's to set (moving its target is what changed)
		var solved = skeleton.IkConstraints.Where(c => c.Mix > 0).SelectMany(c => c.Bones).ToHashSet();

		foreach (var (bone, now) in after.Bones)
		{
			var changed = before.Bones.TryGetValue(bone, out var was) ? Differences(was, now) : PoseChannels.All;
			if (solved.Contains(bone)) changed &= ~PoseChannels.Turn;
			bool held = _bones.TryGetValue(bone, out var old);
			if (changed == PoseChannels.None && !held) continue;
			_bones[bone] = (now, (held ? old.Channels : PoseChannels.None) | changed);
		}
		foreach (var (slot, now) in after.Parts)
		{
			var changed = before.Parts.TryGetValue(slot, out var was) ? Differences(was, now) : PoseChannels.Move | PoseChannels.Turn | PoseChannels.Scale;
			bool held = _parts.TryGetValue(slot, out var old);
			if (changed == PoseChannels.None && !held) continue;
			_parts[slot] = (now, (held ? old.Channels : PoseChannels.None) | changed);
		}
		return Any;
	}

	// What's held is for this frame of this animation (anything held for another goes)
	private void Start(Skeleton skeleton, Animation animation, int frame)
	{
		if (!ReferenceEquals(Skeleton, skeleton) || !ReferenceEquals(Animation, animation) || Frame != frame) Clear();
		Skeleton = skeleton;
		Animation = animation;
		Frame = frame;
	}

	// A colour layer's numbers changed without a key
	public void HoldColor(ColorLayer layer, AdjustValues values, Skeleton skeleton, Animation animation, int frame)
	{
		Start(skeleton, animation, frame);
		_colors[layer] = values;
		layer.Adjust.Now = values;
	}

	public void HoldColor(Asset asset, AssetLayer layer, AdjustValues values, Skeleton skeleton, Animation animation, int frame)
	{
		if (layer.Adjust is not { } adjust) return;
		Start(skeleton, animation, frame);
		_layerColors[layer] = (asset, values);
		adjust.Now = values;
		asset.MarkEdited();
	}

	// Still the frame they were made at? Otherwise they go (another frame, playing, another
	// animation or object, Setup mode)
	public void KeepOnlyAt(Skeleton skeleton, Animation animation, int frame, bool stillPosing)
	{
		if (!Any) return;
		if (!stillPosing || !ReferenceEquals(Skeleton, skeleton) || !ReferenceEquals(Animation, animation) || Frame != frame)
			Clear();
	}

	// On top of the pose the keys made (before the rig's constraints are solved)
	public void Apply(Skeleton skeleton, Animation animation, int frame)
	{
		if (Suspended || !Any || !ReferenceEquals(Skeleton, skeleton) || !ReferenceEquals(Animation, animation) || Frame != frame) return;
		foreach (var (bone, (pose, channels)) in _bones)
		{
			if (channels.HasFlag(PoseChannels.Move)) bone.Position = pose.Position;
			if (channels.HasFlag(PoseChannels.Turn)) bone.Rotation = pose.Rotation;
			if (channels.HasFlag(PoseChannels.Scale)) bone.Scale = pose.Scale;
			if (channels.HasFlag(PoseChannels.Depth))
			{
				bone.Turn = pose.Turn;
				bone.Tilt = pose.Tilt;
			}
		}
		foreach (var (slot, (pose, channels)) in _parts)
		{
			if (channels.HasFlag(PoseChannels.Move)) slot.Offset = pose.Offset;
			if (channels.HasFlag(PoseChannels.Turn)) slot.Rotation = pose.Rotation;
			if (channels.HasFlag(PoseChannels.Scale)) slot.Scale = pose.Scale;
		}
		foreach (var (layer, values) in _colors) layer.Adjust.Now = values;
		foreach (var (layer, (asset, values)) in _layerColors)
			if (layer.Adjust is { } adjust && adjust.Now != values)
			{
				adjust.Now = values;
				asset.MarkEdited();
			}
	}

	// Keys for everything held, at `time` (they stay held: Clear when they're meant to be keys
	// for good). How many bones and parts got keys.
	public int KeyInto(Animation animation, float time)
	{
		foreach (var (bone, (pose, channels)) in _bones) KeyBone(animation, time, bone, pose, channels);
		foreach (var (slot, (pose, channels)) in _parts) KeyPart(animation, time, slot, pose, channels);
		foreach (var (layer, values) in _colors)
			animation.GetOrCreate(layer.Name, n => new ColorLayerTimeline(n)).Track.Add(time, values);
		foreach (var (layer, (_, values)) in _layerColors)
			animation.GetOrCreate(LayerAdjustTimeline.TargetOf(layer), n => new LayerAdjustTimeline(n) { Caption = layer.Name }).Track.Add(time, values);
		return Count;
	}

	// Keys for these bones' and parts' whole pose as it is now (S with nothing held: key the picked)
	public static int KeyNow(Animation animation, float time, IEnumerable<Bone> bones, IEnumerable<Slot> parts)
	{
		int count = 0;
		foreach (var bone in bones)
		{
			KeyBone(animation, time, bone, new BonePose(bone.Position, bone.Rotation, bone.Scale, bone.Turn, bone.Tilt),
				PoseChannels.Move | PoseChannels.Turn | PoseChannels.Scale);
			count++;
		}
		foreach (var slot in parts)
		{
			KeyPart(animation, time, slot, new PartPose(slot.Offset, slot.Rotation, slot.Scale), PoseChannels.All);
			count++;
		}
		return count;
	}

	// Keys hold what's added to the rest pose (place, angle, depth) or what it's multiplied by (scale)
	private static void KeyBone(Animation animation, float time, Bone bone, BonePose pose, PoseChannels channels)
	{
		if (channels.HasFlag(PoseChannels.Move))
			animation.GetOrCreate(bone.Name, n => new TranslateTimeline(n)).Track.Add(time, pose.Position - bone.SetupPosition);
		if (channels.HasFlag(PoseChannels.Turn))
			animation.GetOrCreate(bone.Name, n => new RotateTimeline(n)).Track.Add(time, pose.Rotation - bone.SetupRotation);
		if (channels.HasFlag(PoseChannels.Scale))
			animation.GetOrCreate(bone.Name, n => new ScaleTimeline(n)).Track.Add(time,
				new Vector2(Ratio(pose.Scale.X, bone.SetupScale.X), Ratio(pose.Scale.Y, bone.SetupScale.Y)));
		if (channels.HasFlag(PoseChannels.Depth))
			animation.GetOrCreate(bone.Name, n => new DepthTimeline(n)).Track.Add(time,
				new Vector2(pose.Tilt - bone.SetupTilt, pose.Turn - bone.SetupTurn));
	}

	// A part's placement keys hold its placement as it is (on top of its drawing's own)
	private static void KeyPart(Animation animation, float time, Slot slot, PartPose pose, PoseChannels channels)
	{
		if (channels.HasFlag(PoseChannels.Move))
			animation.GetOrCreate(slot.Name, n => new PartTranslateTimeline(n)).Track.Add(time, pose.Offset);
		if (channels.HasFlag(PoseChannels.Turn))
			animation.GetOrCreate(slot.Name, n => new PartRotateTimeline(n)).Track.Add(time, pose.Rotation);
		if (channels.HasFlag(PoseChannels.Scale))
			animation.GetOrCreate(slot.Name, n => new PartScaleTimeline(n)).Track.Add(time, pose.Scale);
	}

	private static float Ratio(float value, float of) => MathF.Abs(of) < 1e-6f ? value : value / of;

	private const float Tolerance = 1e-5f;

	private static bool Same(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b) <= Tolerance * Tolerance;
	private static bool Same(float a, float b) => MathF.Abs(a - b) <= Tolerance;

	private static PoseChannels Differences(BonePose a, BonePose b) =>
		(Same(a.Position, b.Position) ? PoseChannels.None : PoseChannels.Move) |
		(Same(a.Rotation, b.Rotation) ? PoseChannels.None : PoseChannels.Turn) |
		(Same(a.Scale, b.Scale) ? PoseChannels.None : PoseChannels.Scale) |
		(Same(a.Turn, b.Turn) && Same(a.Tilt, b.Tilt) ? PoseChannels.None : PoseChannels.Depth);

	private static PoseChannels Differences(PartPose a, PartPose b) =>
		(Same(a.Offset, b.Offset) ? PoseChannels.None : PoseChannels.Move) |
		(Same(a.Rotation, b.Rotation) ? PoseChannels.None : PoseChannels.Turn) |
		(Same(a.Scale, b.Scale) ? PoseChannels.None : PoseChannels.Scale);
}
