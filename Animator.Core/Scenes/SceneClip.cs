namespace Animator.Core.Scenes;

// A stretch of the scene timeline where an object plays one of its animations: from Start, for
// Length seconds, beginning Offset seconds into the animation (the part before it is trimmed off).
// A clip longer than what's left of its animation loops it, or holds its last frame (Loop off).
public class SceneClip
{
	public Animation Animation { get; set; }
	public float Start { get; set; }    // scene time, seconds
	public float Length { get; set; }   // seconds on the scene timeline
	public float Offset { get; set; }   // where in the animation the clip starts
	public bool Loop { get; set; }
	public float MixIn { get; set; }    // seconds: fading in from the clip before it (0: a cut)

	public SceneClip(Animation animation, float start, float length)
	{
		Animation = animation;
		Start = start;
		Length = length;
		Loop = animation.Loop;
	}

	public float End => Start + Length;

	public bool Covers(float sceneTime) => sceneTime >= Start && sceneTime < End;

	// The animation's own time at a scene time. Before the clip it waits on its first frame,
	// after it it holds the frame it ended on.
	public float AnimationTime(float sceneTime)
	{
		float t = Offset + Math.Clamp(sceneTime - Start, 0f, MathF.Max(0f, Length));
		float d = Animation.Duration;
		if (d <= 0f) return 0f;
		if (t <= d) return t;
		return Loop ? t % d : d;
	}
}
