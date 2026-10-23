using System.Numerics;

namespace Animator.Core.Editing;

// Keyed detach / attach: changes which bone a bone follows, starting at one frame,
// without the bone jumping on screen.
//   null              = its own parent (normal)
//   SpaceTimeline.World = nothing (detached: thrown, falling off)
//   a bone name       = held by that bone (caught, picked up)
public static class SpaceSwitching
{
	// What the bone follows at this time in this animation
	public static string? SpaceAt(Animation animation, Bone bone, float time)
	{
		var timeline = animation.Find<SpaceTimeline>(bone.Name);
		if (timeline == null || timeline.Track.Keys.Count == 0) return null;
		if (time < timeline.Track.Keys[0].Time - KeyHelpers.Epsilon) return null;
		return timeline.Track.TrySample(time, out var space) ? space : null;
	}

	// The time of the next space key after `time`, if there is one
	public static float? NextSwitchTime(Animation animation, Bone bone, float time)
	{
		var timeline = animation.Find<SpaceTimeline>(bone.Name);
		if (timeline == null) return null;
		foreach (var key in timeline.Track.Keys)
			if (key.Time > time + KeyHelpers.Epsilon) return key.Time;
		return null;
	}

	// A bone can't follow itself or anything below it: that would be a loop
	public static bool CanFollow(Skeleton skeleton, Bone bone, string? space)
	{
		if (space == null || space == SpaceTimeline.World) return true;
		var holder = skeleton.FindBone(space);
		return holder != null && holder != bone && !holder.IsDescendantOf(bone);
	}

	// Switches what `bone` follows from `frame` on. Returns false if nothing changed.
	public static bool Switch(Skeleton skeleton, Animation animation, Bone bone,
							  int frame, int fps, string? newSpace)
	{
		float time = frame / (float)fps;
		if (!CanFollow(skeleton, bone, newSpace)) return false;
		if (SpaceAt(animation, bone, time) == newSpace) return false;

		// Keys after this frame, up to the next switch, were made in the old space.
		// They get re-expressed in the new one, so the motion you already see stays the same.
		float end = NextSwitchTime(animation, bone, time) ?? float.MaxValue;

		var translate = animation.GetOrCreate(bone.Name, n => new TranslateTimeline(n));
		var rotate = animation.GetOrCreate(bone.Name, n => new RotateTimeline(n));
		var scale = animation.Find<ScaleTimeline>(bone.Name);

		var times = new SortedSet<float> { time };
		foreach (Timeline? timeline in new Timeline?[] { translate, rotate, scale })
			if (timeline != null)
				foreach (float t in timeline.KeyTimes)
					if (t > time + KeyHelpers.Epsilon && t < end - KeyHelpers.Epsilon &&
						t <= animation.Duration + KeyHelpers.Epsilon)   // keys past the end never play
						times.Add(t);

		// 1. Measure, with the animation as it is now, what each of those moments looks like
		//    and what the bone's values must become in the new space to look the same
		var targets = new Dictionary<float, LocalPose>();
		foreach (float t in times)
		{
			IkMatching.Evaluate(skeleton, animation, t);
			targets[t] = Measure(skeleton, bone, newSpace);
		}

		// The rotation changes by the difference between the old and new space's angles.
		// Rotation keys interpolate as plain numbers, so that difference is kept continuous
		// from key to key; the bone's own turns (even a 720° spin) are left exactly as they were.
		var rotations = new Dictionary<float, float>();
		float? previousShift = null;
		foreach (float t in times)
		{
			float shift = previousShift is { } prev
				? prev + MathUtil.ShortestAngle(targets[t].RotationShift - prev)
				: MathUtil.ShortestAngle(targets[t].RotationShift);
			previousShift = shift;
			rotations[t] = targets[t].Rotation + shift;
		}

		bool needScale = scale != null ||
						 targets.Values.Any(p => Vector2.DistanceSquared(p.Scale, bone.SetupScale) > 1e-8f);

		// 2. Pin everything one frame earlier, so the frames before the switch don't change
		KeyHelpers.PinBefore(translate, frame, fps, Vector2.Zero);
		KeyHelpers.PinBefore(rotate, frame, fps, 0f);
		if (needScale)
		{
			scale ??= animation.GetOrCreate(bone.Name, n => new ScaleTimeline(n));
			KeyHelpers.PinBefore(scale, frame, fps, Vector2.One);
		}
		var space = animation.GetOrCreate(bone.Name, n => new SpaceTimeline(n));
		KeyHelpers.PinBefore(space, frame, fps, (string?)null);

		// 3. Re-express the later keys
		foreach (float t in times)
		{
			if (t <= time + KeyHelpers.Epsilon) continue;
			KeyHelpers.SetValueAt(translate, t, targets[t].Position - bone.SetupPosition);
			KeyHelpers.SetValueAt(rotate, t, rotations[t] - bone.SetupRotation);
			if (scale != null) KeyHelpers.SetValueAt(scale, t, Ratio(targets[t].Scale, bone.SetupScale));
		}

		// 4. The switch itself
		translate.Track.Add(time, targets[time].Position - bone.SetupPosition);
		rotate.Track.Add(time, rotations[time] - bone.SetupRotation);
		if (scale != null) scale.Track.Add(time, Ratio(targets[time].Scale, bone.SetupScale));
		space.Track.Add(time, newSpace, CurveType.Stepped);
		return true;
	}

	// What the bone's local values must become, measured from `newSpace` instead of what it
	// follows now, to keep its current on-screen transform. The new rotation is
	// Rotation + RotationShift (kept apart so the caller can keep the shift continuous).
	private readonly record struct LocalPose(Vector2 Position, float Rotation, float RotationShift, Vector2 Scale);

	private static LocalPose Measure(Skeleton skeleton, Bone bone, string? newSpace)
	{
		var oldHolder = bone.SpaceParent;
		var newHolder = HolderFor(skeleton, bone, newSpace);

		var world = bone.World.Translation;
		var position = newHolder != null && Matrix3x2.Invert(newHolder.World, out var toNew)
			? Vector2.Transform(world, toNew)
			: world;

		float shift = AngleOf(oldHolder, bone) - AngleOf(newHolder, bone);
		var scale = bone.Scale * ScaleOf(oldHolder, bone) / ScaleOf(newHolder, bone);
		return new LocalPose(position, bone.Rotation, shift, scale);
	}

	// The bone a space name refers to (null = the world)
	public static Bone? HolderFor(Skeleton skeleton, Bone bone, string? space) =>
		space == null ? bone.Parent
		: space == SpaceTimeline.World ? null
		: skeleton.FindBone(space);

	private static float AngleOf(Bone? holder, Bone bone) =>
		holder != null && bone.InheritRotation ? holder.WorldRotation : 0f;

	private static Vector2 ScaleOf(Bone? holder, Bone bone)
	{
		if (holder == null || !bone.InheritScale) return Vector2.One;
		var m = holder.World;
		var s = new Vector2(new Vector2(m.M11, m.M12).Length(), new Vector2(m.M21, m.M22).Length());
		return Vector2.Max(s, new Vector2(1e-4f));   // a holder squashed flat: avoid dividing by zero
	}

	private static Vector2 Ratio(Vector2 a, Vector2 b) => new(
		MathF.Abs(b.X) < 1e-6f ? a.X : a.X / b.X,
		MathF.Abs(b.Y) < 1e-6f ? a.Y : a.Y / b.Y);
}
