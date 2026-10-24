namespace Animator.Core.Keyframing;

// How a timeline's keys behave when frames are cut out of an animation
public enum KeyKind
{
	Blended,       // every frame has a value, from the keys around it (before the first key: its value)
	FromFirstKey,  // held from each key to the next, nothing before the first (drawings, follow spaces)
	Moments        // things that happen at a frame (events)
}

// Cutting a track's keys at frames, keeping how every frame that's left looks. Times are seconds;
// `frame` is one frame's length. Where a cut goes through an eased (Smooth) stretch, that stretch
// can't be eased from a new key the same way, so the frames up to its next key get keys of their own.
internal static class KeyCutting<T>
{
	private const float Eps = 1e-4f;

	// The value playing shows at a time (false where nothing shows yet)
	private static bool ValueAt(Track<T> track, KeyKind kind, float time, out T value)
	{
		value = default!;
		if (track.Keys.Count == 0) return false;
		if (kind != KeyKind.Blended && time < track.Keys[0].Time - Eps) return false;
		return track.TrySample(time, out value);
	}

	// The key whose stretch a time falls in (the last key at or before it), or -1
	private static int StretchAt(Track<T> track, float time) => track.Keys.FindLastIndex(k => k.Time <= time + Eps);

	private static bool KeyedAt(Track<T> track, float time) => track.Keys.Exists(k => MathF.Abs(k.Time - time) < Eps);

	// Keys on every frame from `from` to `to` (both included), straight lines between: an eased
	// stretch played as it was
	private static IEnumerable<Keyframe<T>> Baked(Track<T> track, KeyKind kind, float from, float to, float frame)
	{
		int frames = (int)MathF.Round((to - from) / frame);
		for (int i = 0; i <= frames; i++)
		{
			float t = from + i * frame;
			if (ValueAt(track, kind, t, out var v)) yield return new Keyframe<T>(t, v, CurveType.Linear);
		}
	}

	// A key at `time` that plays the stretch it's in on from there as before (up to `until`, at
	// most): the same curve, or for an eased stretch, a key on each of its frames
	private static IEnumerable<Keyframe<T>> Resume(Track<T> track, KeyKind kind, float time, float until, float frame)
	{
		int s = StretchAt(track, time);
		if (!ValueAt(track, kind, time, out var value)) yield break;
		var curve = s >= 0 ? track.Keys[s].Curve : CurveType.Linear;
		if (curve == CurveType.Smooth && s + 1 < track.Keys.Count)
		{
			foreach (var key in Baked(track, kind, time, MathF.Min(track.Keys[s + 1].Time - frame, until), frame))
				yield return key;
		}
		else yield return new Keyframe<T>(time, value, curve == CurveType.Smooth ? CurveType.Linear : curve);
	}

	// Keeps only from..to (both included), moved back to start at 0
	public static void Crop(Track<T> track, KeyKind kind, float from, float to, float frame)
	{
		var keys = track.Keys;
		var kept = new List<Keyframe<T>>();
		if (kind != KeyKind.Moments && keys.Count > 0)
		{
			// The first frame: what shows there, played on as before
			if (!KeyedAt(track, from)) kept.AddRange(Resume(track, kind, from, to, frame));

			// The last frame of a stretch running past the end: what shows there (eased: frame by frame)
			int e = StretchAt(track, to);
			if (kind == KeyKind.Blended && !KeyedAt(track, to) && e >= 0 && e + 1 < keys.Count)
			{
				if (keys[e].Curve == CurveType.Smooth)
				{
					if (keys[e].Time >= from - Eps) kept.AddRange(Baked(track, kind, keys[e].Time + frame, to, frame));
				}
				else if (ValueAt(track, kind, to, out var atEnd))
				{
					kept.Add(new Keyframe<T>(to, atEnd, CurveType.Linear));
				}
			}
		}
		kept.AddRange(keys.Where(k => k.Time >= from - Eps && k.Time <= to + Eps));
		Replace(track, kept, k => k with { Time = MathF.Max(0, k.Time - from) });
	}

	// Takes out from..to (both included): what's after moves back by the gap; the frames just
	// before and after it look as they did. `end`: the animation's length.
	public static void Cut(Track<T> track, KeyKind kind, float from, float to, float frame, float end)
	{
		var keys = track.Keys;
		float before = from - frame, after = to + frame, gap = to - from + frame;
		var added = new List<Keyframe<T>>();
		if (kind != KeyKind.Moments && keys.Count > 0)
		{
			// The frame before the cut, if its stretch runs into it (blended values only: held
			// ones hold on anyway)
			if (kind == KeyKind.Blended && before >= -Eps && !KeyedAt(track, before) && keys.Exists(k => k.Time >= from - Eps))
			{
				int s = StretchAt(track, before);
				if (s >= 0 && keys[s].Curve == CurveType.Smooth)
					added.AddRange(Baked(track, kind, keys[s].Time + frame, before, frame));
				else if (ValueAt(track, kind, before, out var value))
					added.Add(new Keyframe<T>(before, value, s >= 0 ? keys[s].Curve : CurveType.Linear));
			}

			// The frame after the cut, if what shows there comes from a key that's cut out, or (a
			// blended value) from a stretch running across the cut
			int e = StretchAt(track, after);
			bool fromCutKey = e >= 0 && keys[e].Time >= from - Eps && keys[e].Time <= to + Eps;
			bool across = kind == KeyKind.Blended && e >= 0 && keys[e].Time < from - Eps && e + 1 < keys.Count && keys[e + 1].Time > to + Eps;
			if (after <= end + Eps && !KeyedAt(track, after) && (fromCutKey || across))
				added.AddRange(Resume(track, kind, after, end, frame));
		}

		var kept = keys.Where(k => k.Time < from - Eps || k.Time > to + Eps).Concat(added).ToList();
		Replace(track, kept, k => k.Time > to + Eps ? k with { Time = k.Time - gap } : k);
	}

	// The track's keys become `keys` (moved by `move`), in order, one to a time
	private static void Replace(Track<T> track, List<Keyframe<T>> keys, Func<Keyframe<T>, Keyframe<T>> move)
	{
		var moved = keys.Select(move).OrderBy(k => k.Time).ToList();
		track.Keys.Clear();
		foreach (var key in moved)
			if (track.Keys.Count == 0 || MathF.Abs(track.Keys[^1].Time - key.Time) >= Eps)
				track.Keys.Add(key);
	}
}
