using System.Numerics;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

public static class IkMatching
{
	// Poses the skeleton at a given time. Optionally forces one constraint's Mix,
	// which lets us read "what FK says" and "what IK says" at the same frame.
	public static void Evaluate(Skeleton skeleton, Animation animation, float time,
								IkConstraint? forced = null, float forcedMix = 0f)
	{
		skeleton.SetToSetupPose();
		animation.Apply(skeleton, time);
		if (forced != null) forced.Mix = forcedMix;   // overrides any IkMix keys
		skeleton.UpdatePose();
	}

	// FK -> IK match: put the target where the FK hand is
	public static void SnapTargetToTip(Skeleton skeleton, Animation animation,
									   IkConstraint c, float time, bool key)
	{
		Evaluate(skeleton, animation, time, c, 0f);   // the FK pose
		var tip = c.Bones[^1].TipWorld;
		var target = c.Target;
		var local = target.WorldToParentLocal(tip);

		if (key)
			animation.GetOrCreate(target.Name, n => new TranslateTimeline(n))
					 .Track.Add(time, local - target.SetupPosition);
		else
			target.SetupPosition += local - target.Position;   // same delta trick as dragging
	}

	// IK -> FK match: store the solved rotations as FK values
	public static void BakeIkToFk(Skeleton skeleton, Animation animation,
								  IkConstraint c, float time, bool key)
	{
		Evaluate(skeleton, animation, time, c, 0f);
		var fk = c.Bones.Select(b => b.Rotation).ToArray();

		Evaluate(skeleton, animation, time, c, 1f);
		for (int i = 0; i < c.Bones.Count; i++)
		{
			var bone = c.Bones[i];
			float ik = bone.Rotation;   // already within 180° of the FK value (see Solve)

			if (key)
				animation.GetOrCreate(bone.Name, n => new RotateTimeline(n))
						 .Track.Add(time, ik - bone.SetupRotation);
			else
				bone.SetupRotation += ik - fk[i];
		}
	}

	// The seamless switch: match the pose, then snap the Mix at this exact frame
	public static void SwitchMode(Skeleton skeleton, Animation animation,
								  IkConstraint c, int frame, int fps, bool toIk)
	{
		float time = frame / (float)fps;

		if (toIk)
		{
			KeyHelpers.PinBefore(animation.GetOrCreate(c.Target.Name, n => new TranslateTimeline(n)),
					  frame, fps, Vector2.Zero);
			SnapTargetToTip(skeleton, animation, c, time, key: true);
		}
		else
		{
			foreach (var bone in c.Bones)
				KeyHelpers.PinBefore(animation.GetOrCreate(bone.Name, n => new RotateTimeline(n)),
						  frame, fps, 0f);
			BakeIkToFk(skeleton, animation, c, time, key: true);
		}

		var mix = animation.GetOrCreate(c.Name, n => new IkMixTimeline(n));
		KeyHelpers.PinBefore(mix, frame, fps, c.SetupMix);
		mix.Track.Add(time, toIk ? 1f : 0f, CurveType.Stepped);
	}
}
