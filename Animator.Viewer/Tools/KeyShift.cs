namespace Animator.Viewer.Tools;

// The keys selected in the timeline on one row, with their values when a drag started. A drag
// that has some changes them all alike, so the animation plays as it did, moved (a layer that
// sits a little off all through it, put right in one go), instead of keying the playhead.
internal sealed class KeyShift<T>
{
	private readonly Track<T> _track;
	private readonly List<(int Index, T Start)> _keys = new();

	private KeyShift(Track<T> track) => _track = track;

	// The selected keys on `timeline` (null: none of its keys are selected)
	public static KeyShift<T>? Of(TrackTimeline<T>? timeline, IEnumerable<(Timeline Tl, int Frame)> selected, int fps)
	{
		if (timeline == null) return null;
		var frames = selected.Where(k => k.Tl == timeline).Select(k => k.Frame).ToHashSet();
		if (frames.Count == 0) return null;
		var shift = new KeyShift<T>(timeline.Track);
		var keys = timeline.Track.Keys;
		for (int i = 0; i < keys.Count; i++)
			if (frames.Contains((int)MathF.Round(keys[i].Time * fps)))
				shift._keys.Add((i, keys[i].Value));
		return shift._keys.Count > 0 ? shift : null;
	}

	// Each selected key: its value at the start, changed
	public void Apply(Func<T, T> change)
	{
		var keys = _track.Keys;
		foreach (var (i, start) in _keys)
			if (i < keys.Count) keys[i] = keys[i] with { Value = change(start) };
	}
}
