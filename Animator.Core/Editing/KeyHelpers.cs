namespace Animator.Core.Editing;

// Small keyframe utilities shared by the matching, switching and baking helpers
internal static class KeyHelpers
{
	public const float Epsilon = 1e-4f;

	// Keys the current value one frame earlier, stepped, so a new key at `frame`
	// can't change anything that happens before it. Must run BEFORE that key exists.
	public static void PinBefore<T>(TrackTimeline<T> timeline, int frame, int fps, T fallback)
	{
		if (frame <= 0) return;
		float prev = (frame - 1) / (float)fps;
		if (!timeline.Track.TrySample(prev, out var value)) value = fallback;
		timeline.Track.Add(prev, value, CurveType.Stepped);
	}

	// Changes the value of the key at `time`, if there is one, keeping its curve
	public static void SetValueAt<T>(TrackTimeline<T> timeline, float time, T value)
	{
		var keys = timeline.Track.Keys;
		int i = keys.FindIndex(k => MathF.Abs(k.Time - time) < Epsilon);
		if (i >= 0) keys[i] = keys[i] with { Value = value };
	}
}
