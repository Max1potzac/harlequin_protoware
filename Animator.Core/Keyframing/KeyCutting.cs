namespace Animator.Core.Keyframing;

// How a timeline's keys behave when frames are cut out of an animation
public enum KeyKind
{
	Blended,       // every frame has a value, from the keys around it (before the first key: its value)
	FromFirstKey,  // held from each key to the next, nothing before the first (drawings, follow spaces)
	Moments        // things that happen at a moment (events)
}

// Cutting a track's keys at frames, keeping how every frame that's left looks. Frames are counted
// the way the editor counts them: frame n plays at n / fps seconds. A key within a thousandth of
// a frame of a frame is on it, and every key this makes or moves onto a frame lands exactly there
// (a hair later, and it would show a frame late). A key between frames (an import, a changed frame
// rate) goes with the frame before it. Where a cut goes through an eased (Smooth) stretch, that
// stretch can't be eased from a new key the same way, so its frames up to its next key get keys
// of their own.
internal static class KeyCutting<T>
{
	private const float Grid = 1e-3f;   // frames
	private const float Eps = 1e-4f;    // seconds: keys this close are one key (as on the timeline)

	private static float At(int frame, float fps) => frame / fps;

	// Where a key sits, in frames
	private static float Pos(Keyframe<T> key, float fps) => key.Time * fps;

	// The frame a key sits on, if it sits on one
	private static int? FrameOf(float pos)
	{
		float whole = MathF.Round(pos);
		return MathF.Abs(pos - whole) < Grid ? (int)whole : null;
	}

	// The last frame before a key, and the first one after it
	private static int FrameBefore(float pos) => (int)MathF.Ceiling(pos - Grid) - 1;
	private static int FrameAfter(float pos) => (int)MathF.Floor(pos + Grid) + 1;

	// A key moved back by some frames (exactly onto its frame, if it sits on one)
	private static Keyframe<T> Moved(Keyframe<T> key, int frames, float fps) =>
		key with { Time = FrameOf(Pos(key, fps)) is { } f ? At(f - frames, fps) : key.Time - frames / fps };

	// What playing shows at a frame (false where nothing shows yet)
	private static bool ValueAt(Track<T> track, KeyKind kind, int frame, float fps, out T value)
	{
		value = default!;
		float time = At(frame, fps);
		if (track.Keys.Count == 0 || (kind != KeyKind.Blended && time < track.Keys[0].Time)) return false;
		return track.TrySample(time, out value);
	}

	// The key whose stretch a frame is in (the last key on it or before it), or -1
	private static int StretchAt(Track<T> track, int frame, float fps) =>
		track.Keys.FindLastIndex(k => Pos(k, fps) <= frame + Grid);

	// Eased stretches (Smooth, Bézier) are cut frame by frame: a key on each, so they play as they did
	private static bool Eased(CurveType curve) => curve is CurveType.Smooth or CurveType.Bezier;

	private static bool KeyedAt(Track<T> track, int frame, float fps) =>
		track.Keys.Exists(k => MathF.Abs(Pos(k, fps) - frame) < Grid);

	// Keys on frames from..to (both included), straight lines between: an eased stretch played as it was
	private static IEnumerable<Keyframe<T>> Baked(Track<T> track, KeyKind kind, int from, int to, float fps)
	{
		for (int f = from; f <= to; f++)
			if (ValueAt(track, kind, f, fps, out var v)) yield return new Keyframe<T>(At(f, fps), v, CurveType.Linear);
	}

	// A key on a frame (that has none) playing the stretch it's in on from there as before, up to
	// frame `until` at most: the stretch's curve, or for an eased stretch, a key on each of its frames
	private static IEnumerable<Keyframe<T>> Resume(Track<T> track, KeyKind kind, int frame, int until, float fps)
	{
		if (!ValueAt(track, kind, frame, fps, out var value)) yield break;
		int s = StretchAt(track, frame, fps);
		var curve = s >= 0 ? track.Keys[s].Curve : CurveType.Linear;
		if (kind == KeyKind.Blended && Eased(curve) && s + 1 < track.Keys.Count)
		{
			foreach (var key in Baked(track, kind, frame, Math.Min(FrameBefore(Pos(track.Keys[s + 1], fps)), until), fps))
				yield return key;
		}
		else yield return new Keyframe<T>(At(frame, fps), value, Eased(curve) ? CurveType.Linear : curve);
	}

	// Keeps only frames first..last (both included), moved back to start at frame 0. Moments
	// between the last frame and the next stay too: they happen while the last frame shows.
	public static void Crop(Track<T> track, KeyKind kind, int first, int last, float fps)
	{
		var keys = track.Keys;
		var kept = new List<Keyframe<T>>();
		if (kind == KeyKind.Moments)
		{
			kept.AddRange(keys.Where(k => Pos(k, fps) >= first - Grid && Pos(k, fps) < last + 1 - Grid));
		}
		else if (keys.Count > 0)
		{
			// The first frame: what shows there, played on as before
			if (!KeyedAt(track, first, fps)) kept.AddRange(Resume(track, kind, first, last, fps));

			// The last frame, in a stretch running on past it: what shows there (eased: frame by frame)
			int e = StretchAt(track, last, fps);
			if (kind == KeyKind.Blended && !KeyedAt(track, last, fps) && e >= 0 && e + 1 < keys.Count)
			{
				if (!Eased(keys[e].Curve))
				{
					if (ValueAt(track, kind, last, fps, out var atEnd)) kept.Add(new Keyframe<T>(At(last, fps), atEnd, CurveType.Linear));
				}
				else if (Pos(keys[e], fps) >= first - Grid)   // (eased from before the first frame: done just above)
				{
					kept.AddRange(Baked(track, kind, FrameAfter(Pos(keys[e], fps)), last, fps));
				}
			}
			kept.AddRange(keys.Where(k => Pos(k, fps) >= first - Grid && Pos(k, fps) <= last + Grid));
		}
		Replace(track, kept.Select(k => Moved(k, first, fps)).ToList());
	}

	// Takes out frames first..last (both included), with anything between the last of them and the
	// next frame: the frames after move back to close the gap, and the frames either side of it look
	// as they did. `end`: the animation's length (seconds).
	public static void Cut(Track<T> track, KeyKind kind, int first, int last, float fps, float end)
	{
		var keys = track.Keys;
		int gap = last - first + 1, after = last + 1, endFrame = (int)MathF.Round(end * fps);
		bool Stays(Keyframe<T> k) => Pos(k, fps) < first - Grid;    // before the cut
		bool Moves(Keyframe<T> k) => Pos(k, fps) >= after - Grid;   // from the frame after it on
		var added = new List<Keyframe<T>>();
		if (kind != KeyKind.Moments && keys.Count > 0)
		{
			// The frame before the cut, in a stretch running on to a key that goes or moves (blended
			// values only: held ones hold on anyway)
			int s = StretchAt(track, first - 1, fps);
			if (kind == KeyKind.Blended && first > 0 && !KeyedAt(track, first - 1, fps) && s + 1 < keys.Count && !Stays(keys[s + 1]))
			{
				if (s >= 0 && Eased(keys[s].Curve))
					added.AddRange(Baked(track, kind, FrameAfter(Pos(keys[s], fps)), first - 1, fps));
				else if (ValueAt(track, kind, first - 1, fps, out var value))
					added.Add(new Keyframe<T>(At(first - 1, fps), value, s >= 0 ? keys[s].Curve : CurveType.Linear));
			}

			// The frame after the cut, if what shows there comes from a key that goes, or (a blended
			// value) from a stretch running across the cut
			int e = StretchAt(track, after, fps);
			bool fromCutKey = e >= 0 && !Stays(keys[e]) && !Moves(keys[e]);
			bool across = kind == KeyKind.Blended && e >= 0 && Stays(keys[e]) && e + 1 < keys.Count;
			if (after <= endFrame && !KeyedAt(track, after, fps) && (fromCutKey || across))
				added.AddRange(Resume(track, kind, after, endFrame, fps));
		}
		var kept = keys.Where(k => Stays(k) || Moves(k)).Concat(added);
		Replace(track, kept.Select(k => Moves(k) ? Moved(k, gap, fps) : k).ToList());
	}

	// The track's keys become these, in order, one to a time
	private static void Replace(Track<T> track, List<Keyframe<T>> keys)
	{
		var sorted = keys.OrderBy(k => k.Time).ToList();
		track.Keys.Clear();
		foreach (var key in sorted)
			if (track.Keys.Count == 0 || MathF.Abs(track.Keys[^1].Time - key.Time) >= Eps)
				track.Keys.Add(key with { Time = MathF.Max(0f, key.Time) });
	}
}
