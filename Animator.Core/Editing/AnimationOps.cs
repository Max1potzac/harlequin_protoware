namespace Animator.Core.Editing;

// Cutting an animation into animation sequences: a run of its frames becomes an animation of
// its own (moved to start at frame 0), either copied out or split off (taken out of the
// original, whose later frames move up to close the gap). Every frame looks as it did.
public static class AnimationOps
{
	// Frames first..last (both included) as an animation of their own
	public static Animation Extract(Animation source, int first, int last, int fps, string name)
	{
		var (from, to, frame) = Times(first, last, fps);
		var piece = ProjectMapper.CloneAnimation(source, name);
		foreach (var timeline in piece.Timelines) timeline.Crop(from, to, frame);
		piece.Timelines.RemoveAll(t => !t.KeyTimes.Any());   // nothing happens on it in these frames
		piece.Duration = (last - first + 1) * frame;
		return piece;
	}

	// Takes frames first..last out of an animation: the frames after them move up
	public static void Cut(Animation animation, int first, int last, int fps)
	{
		var (from, to, frame) = Times(first, last, fps);
		foreach (var timeline in animation.Timelines) timeline.Cut(from, to, frame, animation.Duration);
		animation.Timelines.RemoveAll(t => !t.KeyTimes.Any());
		animation.Duration = MathF.Max(frame, animation.Duration - (last - first + 1) * frame);
	}

	// Frames first..last as a new animation, taken out of `source`
	public static Animation Split(Animation source, int first, int last, int fps, string name)
	{
		var piece = Extract(source, first, last, fps, name);
		Cut(source, first, last, fps);
		return piece;
	}

	private static (float From, float To, float Frame) Times(int first, int last, int fps)
	{
		if (fps < 1) throw new ArgumentOutOfRangeException(nameof(fps));
		if (first < 0 || last < first) throw new ArgumentOutOfRangeException(nameof(last), "The frames run backwards.");
		float frame = 1f / fps;
		return (first * frame, last * frame, frame);
	}
}
