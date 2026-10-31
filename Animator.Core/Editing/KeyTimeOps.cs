using Animator.Core.Keyframing;

namespace Animator.Core.Editing;

// Moving keys about in time, the timeline's tools: nudging, stretching, reversing, and frames
// inserted or taken out at a frame. Keys are counted in frames (frame n is at n / fps seconds).
// A key that lands where another key of its row is replaces it.
public static class KeyTimeOps
{
	public static int FrameOf(float time, int fps) => (int)MathF.Round(time * fps);

	// These keys, each to the frame `to` gives it, all at once (taken out first, so one moving
	// onto a spot another hasn't left yet doesn't matter). Where they went.
	public static List<(Timeline Tl, int Frame)> Move(IEnumerable<(Timeline Tl, int Frame)> keys, Func<int, int> to, int fps)
	{
		var taken = keys.Distinct()
			.Select(k => (k.Tl, Frame: to(k.Frame), Key: k.Tl.TakeKey(k.Frame / (float)fps)))
			.ToList();
		var placed = new List<(Timeline, int)>();
		foreach (var (tl, frame, key) in taken)
		{
			if (key == null) continue;
			tl.PutKey(key, frame / (float)fps);
			placed.Add((tl, frame));
		}
		return placed.Distinct().ToList();
	}

	// By some frames (kept from going before frame 0 or past `last`)
	public static List<(Timeline Tl, int Frame)> Nudge(IReadOnlyCollection<(Timeline Tl, int Frame)> keys, int by, int last, int fps)
	{
		if (keys.Count == 0) return new();
		by = Math.Clamp(by, -keys.Min(k => k.Frame), last - keys.Max(k => k.Frame));
		return Move(keys, f => f + by, fps);
	}

	// Further apart (factor > 1: slower) or closer together, counted from frame `around`
	public static List<(Timeline Tl, int Frame)> Stretch(IReadOnlyCollection<(Timeline Tl, int Frame)> keys, int around, float factor, int last, int fps) =>
		Move(keys, f => Math.Clamp(around + (int)MathF.Round((f - around) * factor), 0, last), fps);

	// Back to front, between the first and the last of them
	public static List<(Timeline Tl, int Frame)> Reverse(IReadOnlyCollection<(Timeline Tl, int Frame)> keys, int fps)
	{
		if (keys.Count == 0) return new();
		int first = keys.Min(k => k.Frame), last = keys.Max(k => k.Frame);
		return Move(keys, f => first + last - f, fps);
	}

	// Every key of the animation at these frames
	public static List<(Timeline Tl, int Frame)> KeysWhere(Animation animation, Func<int, bool> frame, int fps) =>
		animation.Timelines
			.SelectMany(tl => tl.KeyTimes.Select(t => (Tl: tl, Frame: FrameOf(t, fps))))
			.Where(k => frame(k.Frame))
			.ToList();

	// `count` new frames after frame `at`: every key after it moves on, and the animation gets
	// that much longer (what's at `at` lasts longer)
	public static void InsertFrames(Animation animation, int at, int count, int fps)
	{
		if (count <= 0) return;
		Move(KeysWhere(animation, f => f > at, fps), f => f + count, fps);
		animation.Duration += count / (float)fps;
	}

	// Frames at..at+count-1 taken out: their keys go, the keys after move up, and the animation
	// gets shorter (it keeps one frame at least). Every other frame looks as it did.
	public static int RemoveFrames(Animation animation, int at, int count, int fps)
	{
		int frames = FrameOf(animation.Duration, fps);
		count = Math.Min(count, Math.Min(frames - at, frames - 1));
		if (at < 0 || count <= 0) return 0;
		AnimationOps.Cut(animation, at, at + count - 1, fps);
		return count;
	}
}
