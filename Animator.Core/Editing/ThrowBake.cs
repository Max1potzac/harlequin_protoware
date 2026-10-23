using System.Numerics;

namespace Animator.Core.Editing;

// Settings for a thrown or falling bone. Units are world units (pixels) and seconds.
public sealed record ThrowSettings
{
	public Vector2 Velocity { get; init; } = new(250, 450);   // launch speed; +Y is up
	public float SpinDegrees { get; init; } = 360;            // degrees per second, + = counter-clockwise
	public float Gravity { get; init; } = 1600;               // pull downwards, per second squared
	public float GroundY { get; init; }                       // height of the floor
	public float Bounce { get; init; } = 0.35f;               // 0 = thud, 1 = bounces forever
	public float Friction { get; init; } = 0.5f;              // 0 = slides forever, 1 = stops dead
	public int KeyEvery { get; init; } = 2;                   // frames between baked keys
}

// Makes a bone fly from one frame on like a thrown object (a falling arm, a dropped gun),
// and bakes the flight into ordinary keys you can tweak afterwards.
public static class ThrowBake
{
	private const int SubSteps = 8;            // physics steps per frame, for steady bounces
	private const float RestSpeed = 40f;       // bounces slower than this just settle

	public static bool Bake(Skeleton skeleton, Animation animation, Bone bone, int frame, int fps, ThrowSettings s)
	{
		float time = frame / (float)fps;

		// Fly until the animation ends, or until the bone is attached again (caught).
		// Checked before changing anything, so "nothing to do" really changes nothing.
		int lastFrame = (int)MathF.Floor(animation.Duration * fps + 1e-3f);
		bool caught = false;
		if (SpaceSwitching.NextSwitchTime(animation, bone, time) is { } next)
		{
			int beforeCatch = (int)MathF.Round(next * fps) - 1;
			caught = beforeCatch <= lastFrame;
			lastFrame = Math.Min(lastFrame, beforeCatch);
		}
		if (lastFrame <= frame) return false;
		float end = lastFrame / (float)fps;

		// A thrown bone follows nothing: detach it here if it isn't already
		if (SpaceSwitching.SpaceAt(animation, bone, time) != SpaceTimeline.World)
			SpaceSwitching.Switch(skeleton, animation, bone, frame, fps, SpaceTimeline.World);

		// Start from exactly where the bone is at the launch frame (now in world space)
		IkMatching.Evaluate(skeleton, animation, time);
		var position = bone.Position;
		float rotation = bone.Rotation;

		var translate = animation.GetOrCreate(bone.Name, n => new TranslateTimeline(n));
		var rotate = animation.GetOrCreate(bone.Name, n => new RotateTimeline(n));

		// The flight replaces whatever this bone did after the launch.
		// The launch frame itself gets a key, so removing later keys can't shift where it starts.
		translate.Track.Keys.RemoveAll(k => k.Time > time + KeyHelpers.Epsilon && k.Time <= end + KeyHelpers.Epsilon);
		rotate.Track.Keys.RemoveAll(k => k.Time > time + KeyHelpers.Epsilon && k.Time <= end + KeyHelpers.Epsilon);
		translate.Track.Add(time, position - bone.SetupPosition, CurveType.Linear);
		rotate.Track.Add(time, rotation - bone.SetupRotation, CurveType.Linear);

		var velocity = s.Velocity;
		float spin = s.SpinDegrees * MathF.PI / 180f;
		float dt = 1f / fps / SubSteps;
		bool onGround = false;
		int keyEvery = Math.Max(1, s.KeyEvery);

		for (int f = frame + 1; f <= lastFrame; f++)
		{
			for (int i = 0; i < SubSteps; i++)
			{
				if (!onGround)
				{
					velocity.Y -= s.Gravity * dt;
					position += velocity * dt;
					rotation += spin * dt;

					if (position.Y <= s.GroundY && velocity.Y < 0)   // hit the floor
					{
						position.Y = s.GroundY;
						velocity.Y = -velocity.Y * Math.Clamp(s.Bounce, 0f, 1f);
						velocity.X *= 1 - Math.Clamp(s.Friction, 0f, 1f) * 0.5f;
						spin *= 1 - Math.Clamp(s.Friction, 0f, 1f) * 0.5f;
						if (velocity.Y < RestSpeed)
						{
							velocity.Y = 0;
							onGround = true;
						}
					}
				}
				else
				{
					// Sliding and rolling along the floor, slowing down
					float slow = MathF.Max(0f, 1 - Math.Clamp(s.Friction, 0f, 1f) * 10f * dt);
					velocity.X *= slow;
					spin *= slow;
					position.X += velocity.X * dt;
					rotation += spin * dt;
				}
			}

			if ((f - frame) % keyEvery == 0 || f == lastFrame)
			{
				// The key just before a catch is stepped: the catch's keys are in the
				// catcher's space, so the flight mustn't slide toward them
				float t = f / (float)fps;
				var curve = f == lastFrame && caught ? CurveType.Stepped : CurveType.Linear;
				translate.Track.Add(t, position - bone.SetupPosition, curve);
				rotate.Track.Add(t, rotation - bone.SetupRotation, curve);
			}
		}
		return true;
	}
}
