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

	// The time of the next switch after `time`: the next space key that makes the bone follow
	// something else than it does at `time`. (A key that repeats the same space isn't a switch.)
	public static float? NextSwitchTime(Animation animation, Bone bone, float time) =>
		NextSwitchAway(animation, bone, time, SpaceAt(animation, bone, time));

	// The time of the next space key after `time` that makes the bone follow anything but `space`
	public static float? NextSwitchAway(Animation animation, Bone bone, float time, string? space)
	{
		var timeline = animation.Find<SpaceTimeline>(bone.Name);
		if (timeline == null) return null;
		foreach (var key in timeline.Track.Keys)
			if (key.Time > time + KeyHelpers.Epsilon && key.Value != space) return key.Time;
		return null;
	}

	// A bone can't follow itself, or anything that follows it (directly or further up,
	// like its own children): that would be a loop. Goes by what every bone follows in
	// the pose the skeleton is in right now, so pose the frame first.
	public static bool CanFollow(Skeleton skeleton, Bone bone, string? space)
	{
		if (space == null || space == SpaceTimeline.World) return true;
		var holder = skeleton.FindBone(space);
		if (holder == null) return false;

		var seen = new HashSet<Bone>();   // in case a loop was already made somewhere else
		for (var b = holder; b != null && seen.Add(b); b = b.SpaceParent)
			if (b == bone) return false;
		return true;
	}

	// Switches what `bone` follows from `frame` on. Returns false if nothing changed.
	//
	// At the switch frame nothing moves: compensating keys re-express the bone's current
	// on-screen position, rotation and scale in the new space. After it, the bone's own
	// position, rotation and scale keys (up to the next switch) are removed, because they were
	// made for the old space: the bone simply stays where it is in its new space (lying still
	// when detached, carried along when held) until you animate it, or throw it.
	public static bool Switch(Skeleton skeleton, Animation animation, Bone bone,
							  int frame, int fps, string? newSpace)
	{
		float time = frame / (float)fps;
		if (SpaceAt(animation, bone, time) == newSpace) return false;

		// 1. Measure the pose at the switch, as the animation is now
		IkMatching.Evaluate(skeleton, animation, time);
		if (!CanFollow(skeleton, bone, newSpace)) return false;
		var target = Measure(skeleton, bone, newSpace);
		// Keep the new rotation within half a turn of the old one, so nothing spins
		float rotation = target.Rotation + MathUtil.ShortestAngle(target.RotationShift);

		// Detaching (or handing over) a bone breaks any IK chain it's in, and IK lets go
		// of a broken chain. So the chain's other bones keep their IK pose as FK keys.
		// (A chain that is already broken has nothing to keep: it's playing FK already.)
		var chainPose = new Dictionary<Bone, float>();
		if (newSpace != null)
			foreach (var ik in skeleton.IkConstraints.Where(c =>
						 c.Mix > 0f && c.Bones.Contains(bone) && !c.Bones.Any(b => b.SpaceOverridden)))
				foreach (var other in ik.Bones.Where(b => b != bone))
					chainPose[other] = other.Rotation;

		// Re-attaching mends such a chain, and IK would yank it toward its target on this
		// frame. So the chain stays on FK from here; Shift+I hands it back to IK without a jump.
		var mended = newSpace == null
			? skeleton.IkConstraints.Where(c => c.Mix > 0f && c.Bones.Contains(bone)
											   && c.Bones.All(b => b == bone || !b.SpaceOverridden)).ToList()
			: new List<IkConstraint>();

		var translate = animation.GetOrCreate(bone.Name, n => new TranslateTimeline(n));
		var rotate = animation.GetOrCreate(bone.Name, n => new RotateTimeline(n));
		var scale = animation.Find<ScaleTimeline>(bone.Name);
		bool needScale = scale != null || Vector2.DistanceSquared(target.Scale, bone.SetupScale) > 1e-8f;
		var space = animation.GetOrCreate(bone.Name, n => new SpaceTimeline(n));

		// 2. Pin everything one frame earlier, so the frames before the switch don't change.
		//    (The space row needs no pin: a space key never reaches back before itself,
		//    and a pin there would look like one more switch.)
		KeyHelpers.PinBefore(translate, frame, fps, Vector2.Zero);
		KeyHelpers.PinBefore(rotate, frame, fps, 0f);
		if (needScale)
		{
			scale ??= animation.GetOrCreate(bone.Name, n => new ScaleTimeline(n));
			KeyHelpers.PinBefore(scale, frame, fps, Vector2.One);
		}
		foreach (var other in chainPose.Keys)
			KeyHelpers.PinBefore(animation.GetOrCreate(other.Name, n => new RotateTimeline(n)), frame, fps, 0f);

		// 3. This switch lasts until the next one that changes the space again. Clear the bone's
		//    old-space keys up to there. Space keys in between can only repeat the new space,
		//    so they merge into this switch (detaching earlier replaces a later detach).
		float? next = NextSwitchAway(animation, bone, time, newSpace);
		float end = next ?? float.MaxValue;
		bool Later(float t) => t > time + KeyHelpers.Epsilon && t < end - KeyHelpers.Epsilon;
		space.Track.Keys.RemoveAll(k => Later(k.Time));
		translate.Track.Keys.RemoveAll(k => Later(k.Time));
		rotate.Track.Keys.RemoveAll(k => Later(k.Time));
		scale?.Track.Keys.RemoveAll(k => Later(k.Time));

		// 4. The switch itself
		var position = target.Position - bone.SetupPosition;
		float turn = rotation - bone.SetupRotation;
		var stretch = Ratio(target.Scale, bone.SetupScale);
		translate.Track.Add(time, position);
		rotate.Track.Add(time, turn);
		scale?.Track.Add(time, stretch);
		space.Track.Add(time, newSpace, CurveType.Stepped);

		// 5. Hold still until the next switch. Its keys are in the space it starts, so nothing
		//    may slide toward them before it (that's why the last key before it is stepped).
		if (next is { } nextTime)
		{
			float hold = MathF.Max(time, (MathF.Round(nextTime * fps) - 1) / fps);
			translate.Track.Add(hold, position, CurveType.Stepped);
			rotate.Track.Add(hold, turn, CurveType.Stepped);
			scale?.Track.Add(hold, stretch, CurveType.Stepped);
		}

		// 6. The rest of a broken IK chain holds the pose IK gave it at this frame
		foreach (var (other, otherRotation) in chainPose)
			animation.GetOrCreate(other.Name, n => new RotateTimeline(n))
					 .Track.Add(time, otherRotation - other.SetupRotation);

		// 7. A mended chain stays on FK
		foreach (var c in mended)
		{
			var mix = animation.GetOrCreate(c.Name, n => new IkMixTimeline(n));
			KeyHelpers.PinBefore(mix, frame, fps, c.SetupMix);
			mix.Track.Add(time, 0f, CurveType.Stepped);
		}
		return true;
	}

	// What the bone's local values must become, measured from `newSpace` instead of what it
	// follows now, to keep its current on-screen transform. The new rotation is
	// Rotation + RotationShift (kept apart so the shift can be kept small).
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
