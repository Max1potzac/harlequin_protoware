namespace Animator.Core.Editing;

// Cutting an animation into animation sequences: a run of its frames becomes an animation of
// its own (moved to start at frame 0), either copied out or split off (taken out of the
// original, whose later frames move up to close the gap). Every frame looks as it did.
// Frames are counted as the editor counts them: frame n plays at n / fps seconds.
public static class AnimationOps
{
	// Frames first..last (both included) as an animation of their own
	public static Animation Extract(Animation source, int first, int last, int fps, string name)
	{
		Check(first, last, fps);
		var piece = ProjectMapper.CloneAnimation(source, name);
		foreach (var timeline in piece.Timelines) timeline.Crop(first, last, fps);
		piece.Timelines.RemoveAll(t => !t.KeyTimes.Any());   // nothing happens on it in these frames
		piece.Duration = (last - first + 1) / (float)fps;
		return piece;
	}

	// Takes frames first..last out of an animation: the frames after them move up
	public static void Cut(Animation animation, int first, int last, int fps)
	{
		Check(first, last, fps);
		int frames = (int)MathF.Round(animation.Duration * fps);
		foreach (var timeline in animation.Timelines) timeline.Cut(first, last, fps, animation.Duration);
		animation.Timelines.RemoveAll(t => !t.KeyTimes.Any());
		animation.Duration = Math.Max(1, frames - (last - first + 1)) / (float)fps;
	}

	// Frames first..last as a new animation, taken out of `source`
	public static Animation Split(Animation source, int first, int last, int fps, string name)
	{
		var piece = Extract(source, first, last, fps, name);
		Cut(source, first, last, fps);
		return piece;
	}

	private static void Check(int first, int last, int fps)
	{
		if (fps < 1) throw new ArgumentOutOfRangeException(nameof(fps));
		if (first < 0 || last < first) throw new ArgumentOutOfRangeException(nameof(last), "The frames run backwards.");
	}
}
