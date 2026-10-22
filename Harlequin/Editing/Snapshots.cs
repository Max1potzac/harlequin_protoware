using System.Numerics;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// Every timeline and its keys, at one moment
public sealed class AnimationSnapshot
{
	private readonly Animation _animation;
	private readonly List<(Timeline Timeline, object Keys)> _saved;

	private AnimationSnapshot(Animation animation)
	{
		_animation = animation;
		_saved = animation.Timelines.Select(t => (t, t.CaptureKeys())).ToList();
	}

	public static AnimationSnapshot Capture(Animation animation) => new(animation);

	public void Restore()
	{
		// Restoring the list also removes timelines created after the snapshot
		_animation.Timelines.Clear();
		foreach (var (timeline, keys) in _saved)
		{
			timeline.RestoreKeys(keys);
			_animation.Timelines.Add(timeline);
		}
	}
}
// Every bone's rest pose + the animation
public sealed class RigSnapshot
{
	private readonly List<(Bone Bone, Vector2 Position, float Rotation, Vector2 Scale)> _bones;
	private readonly AnimationSnapshot _animation;

	private RigSnapshot(Skeleton skeleton, Animation animation)
	{
		_bones = skeleton.AllBones()
			.Select(b => (b, b.SetupPosition, b.SetupRotation, b.SetupScale))
			.ToList();
		_animation = AnimationSnapshot.Capture(animation);
	}

	public static RigSnapshot Capture(Skeleton skeleton, Animation animation) => new(skeleton, animation);

	public void Restore()
	{
		foreach (var (bone, position, rotation, scale) in _bones)
		{
			bone.SetupPosition = position;
			bone.SetupRotation = rotation;
			bone.SetupScale = scale;
		}
		_animation.Restore();
	}
}
// One bone's rest pose + the animation. Covers drags in both modes.
public sealed class PoseSnapshot
{
	private readonly Bone _bone;
	private readonly Vector2 _position, _scale;
	private readonly float _rotation;
	private readonly AnimationSnapshot _animation;

	private PoseSnapshot(Bone bone, Animation animation)
	{
		_bone = bone;
		_position = bone.SetupPosition;
		_rotation = bone.SetupRotation;
		_scale = bone.SetupScale;
		_animation = AnimationSnapshot.Capture(animation);
	}

	public static PoseSnapshot Capture(Bone bone, Animation animation) => new(bone, animation);

	public void Restore()
	{
		_bone.SetupPosition = _position;
		_bone.SetupRotation = _rotation;
		_bone.SetupScale = _scale;
		_animation.Restore();
	}
}