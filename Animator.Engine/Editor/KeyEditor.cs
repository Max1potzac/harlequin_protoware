namespace Animator.Viewer;

// The keys of the animation being edited, edited (what the timeline, its menus and the keys'
// shortcuts call): the selected keys' curve, deleted, copied and pasted, nudged, stretched,
// reversed; keys selected by frame or by what they move; frames inserted and removed; the pose
// keyed (S) or held (auto key off); the playhead to the next key. Each edit is one undo step.
// The selection is EditorState.SelectedKeys: (row, frame) pairs.
public sealed class KeyEditor
{
	private readonly EditorState _state;
	// Copied keys: their rows, frames counted from the earliest one, and the keys
	private readonly List<(Timeline Tl, int RelFrame, object Key)> _clipboard = new();

	public KeyEditor(EditorState state) => _state = state;

	private HashSet<(Timeline Tl, int Frame)> Sel => _state.SelectedKeys;
	private float FrameToTime(int frame) => frame / (float)_state.Fps;
	private int TimeToFrame(float time) => (int)MathF.Round(time * _state.Fps);

	// One change to the animation as one undo step
	public void Edit(string name, Action change)
	{
		var before = AnimationSnapshot.Capture(_state.Animation);
		change();
		_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(name, before, AnimationSnapshot.Capture(_state.Animation), s => s.Restore()));
	}

	public bool CanPaste => _clipboard.Count > 0;

	// ======================= the selected keys =======================

	// The curve all the selected keys share (null: they differ, or none is selected)
	public CurveType? SelectedCurve()
	{
		CurveType? result = null;
		foreach (var (tl, f) in Sel)
			foreach (var (t, c) in tl.KeyInfos)
			{
				if (TimeToFrame(t) != f) continue;
				if (result == null) result = c;
				else if (result != c) return null;
			}
		return result;
	}

	public bool SetCurve(CurveType curve)
	{
		if (Sel.Count == 0) return false;
		Edit($"Set {Sel.Count} key(s) to {curve}", () =>
		{
			foreach (var (tl, f) in Sel) tl.SetCurve(FrameToTime(f), curve);
		});
		return true;
	}

	// Eased by these Bézier handles (from each key to the next)
	public bool SetBezier(Bezier bezier, string name)
	{
		if (Sel.Count == 0) return false;
		Edit($"{Sel.Count} key(s): {name}", () =>
		{
			foreach (var (tl, f) in Sel) tl.SetBezier(FrameToTime(f), bezier);
		});
		return true;
	}

	public bool DeleteSelected()
	{
		if (Sel.Count == 0) return false;
		Edit($"Delete {Sel.Count} key(s)", () =>
		{
			foreach (var (tl, f) in Sel) tl.RemoveKey(FrameToTime(f));
		});
		Sel.Clear();
		return true;
	}

	public bool Copy()
	{
		if (Sel.Count == 0) return false;
		int min = Sel.Min(k => k.Frame);
		_clipboard.Clear();
		foreach (var (tl, f) in Sel)
			if (tl.CopyKey(FrameToTime(f)) is { } key) _clipboard.Add((tl, f - min, key));
		return true;
	}

	// The copied keys into the same rows, the earliest on the playhead (keys past the end, and
	// rows undone out of existence, left out); they're selected
	public int Paste()
	{
		if (_clipboard.Count == 0) return 0;
		int at = _state.Frame;
		var pasted = new List<(Timeline, int)>();
		Edit($"Paste {_clipboard.Count} key(s) at frame {at}", () =>
		{
			foreach (var (tl, rel, key) in _clipboard)
			{
				int f = at + rel;
				if (f > _state.FrameCount || !_state.Animation.Timelines.Contains(tl)) continue;
				tl.PutKey(key, FrameToTime(f));
				pasted.Add((tl, f));
			}
		});
		Reselect(pasted);
		return pasted.Count;
	}

	// Some frames earlier (negative) or later, inside the animation
	public bool Nudge(int by)
	{
		if (Sel.Count == 0 || by == 0) return false;
		var keys = Sel.ToList();
		Edit($"Nudge {keys.Count} key(s) {(by < 0 ? "earlier" : "later")}", () => Reselect(KeyTimeOps.Nudge(keys, by, _state.FrameCount, _state.Fps)));
		return true;
	}

	// Further apart or closer together (factor 2 = twice as long), from the first of them
	public bool Stretch(float factor)
	{
		if (Sel.Count < 2 || !(factor > 0)) return false;
		var keys = Sel.ToList();
		int from = keys.Min(k => k.Frame);
		Edit($"Stretch {keys.Count} keys to {factor * 100:0}%", () => Reselect(KeyTimeOps.Stretch(keys, from, factor, _state.FrameCount, _state.Fps)));
		return true;
	}

	// Back to front, between the first and the last of them
	public bool Reverse()
	{
		if (Sel.Count < 2) return false;
		var keys = Sel.ToList();
		Edit($"Reverse {keys.Count} keys", () => Reselect(KeyTimeOps.Reverse(keys, _state.Fps)));
		return true;
	}

	private void Reselect(IEnumerable<(Timeline Tl, int Frame)> keys)
	{
		Sel.Clear();
		Sel.UnionWith(keys);
	}

	// ======================= selecting =======================

	// Every key of these bones and parts (a part: its own rows) becomes the selection; how many
	public int SelectKeysOf(IEnumerable<Bone> bones, IEnumerable<Slot> parts)
	{
		var boneNames = bones.Select(b => b.Name).ToHashSet();
		var partNames = parts.Select(s => s.Name).ToHashSet();
		Sel.Clear();
		foreach (var tl in _state.Animation.Timelines)
		{
			bool mine = (RigOps.TargetsBone(tl) && boneNames.Contains(tl.Target)) || (PartOps.TargetsPart(tl) && partNames.Contains(tl.Target));
			if (!mine) continue;
			foreach (var time in tl.KeyTimes) Sel.Add((tl, TimeToFrame(time)));
		}
		return Sel.Count;
	}

	// Every key of these rows at a frame, or from it on, becomes the selection; how many
	public int SelectKeys(IEnumerable<Timeline> rows, int frame, bool fromOn)
	{
		Sel.Clear();
		foreach (var tl in rows)
			foreach (var t in tl.KeyTimes)
			{
				int f = TimeToFrame(t);
				if (fromOn ? f >= frame : f == frame) Sel.Add((tl, f));
			}
		return Sel.Count;
	}

	// Every key of these rows at a frame
	public List<(Timeline Tl, int Frame)> KeysAt(IEnumerable<Timeline> rows, int frame) =>
		rows.SelectMany(tl => tl.KeyTimes.Select(t => (Tl: tl, Frame: TimeToFrame(t)))).Where(k => k.Frame == frame).ToList();

	// ======================= frames =======================

	// New frames after `at` (every later key moves on, the animation gets longer)
	public bool InsertFrames(int at, int count)
	{
		if (count <= 0) return false;
		Sel.Clear();
		Edit(count == 1 ? $"Insert a frame after frame {at}" : $"Insert {count} frames after frame {at}",
			() => KeyTimeOps.InsertFrames(_state.Animation, at, count, _state.Fps));
		return true;
	}

	// Frames at..at+count-1 taken out (their keys go, later keys move up); the playhead stays
	// inside. False: nothing to take.
	public bool RemoveFrames(int at, int count)
	{
		var before = AnimationSnapshot.Capture(_state.Animation);
		int removed = KeyTimeOps.RemoveFrames(_state.Animation, at, count, _state.Fps);
		if (removed == 0) return false;
		Sel.Clear();
		_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
			removed == 1 ? $"Remove frame {at}" : $"Remove frames {at}–{at + removed - 1}",
			before, AnimationSnapshot.Capture(_state.Animation), s => s.Restore()));
		_state.SetFrame(Math.Min(at, _state.FrameCount));
		return true;
	}

	// The playhead to the next key of these rows after it (or the one before). False: none
	public bool JumpToKey(IEnumerable<Timeline> rows, int direction)
	{
		int now = _state.Frame;
		var frames = rows.SelectMany(tl => tl.KeyTimes).Select(TimeToFrame).Distinct().ToList();
		int next = direction > 0 ? frames.Where(f => f > now).DefaultIfEmpty(-1).Min() : frames.Where(f => f < now).DefaultIfEmpty(-1).Max();
		if (next < 0) return false;
		_state.Playing = false;
		_state.SetFrame(next);
		return true;
	}

	// ======================= keying the pose =======================

	// S: keys at the playhead (on its frame) for the unkeyed changes (auto key off), or else for
	// the picked bones' and parts' pose as it is. One undo step. How many keys (0: nothing to key).
	public int KeyPose()
	{
		_state.Playing = false;
		_state.SetFrame(_state.Frame);
		var animation = _state.Animation;
		var before = AnimationSnapshot.Capture(animation);
		bool held = _state.Hold.Any;
		int count = held
			? _state.Hold.KeyInto(animation, _state.Time)
			: PoseHold.KeyNow(animation, _state.Time, _state.PickedBones, _state.PickedSlots);
		if (count == 0) return 0;
		_state.Hold.Clear();
		_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
			$"Key {count} {(held ? "posed" : "picked")} @ frame {_state.Frame}",
			before, AnimationSnapshot.Capture(animation), s => s.Restore()));
		return count;
	}

	// Auto key off: a pose change (typed in a panel) held until it's keyed, instead of a key.
	// False: auto key is on, so it's for a key (nothing was done).
	public bool HoldInsteadOfKey(Action pose)
	{
		if (_state.AutoKey) return false;
		_state.Playing = false;
		_state.SetFrame(_state.Frame);
		var skeleton = _state.Skeleton;
		var before = PoseValues.Capture(skeleton);
		pose();
		_state.Hold.Take(before, PoseValues.Capture(skeleton), skeleton, _state.Animation, _state.Frame);
		skeleton.UpdatePose();
		return true;
	}

	// The keys of the picked bones and parts at the playhead deleted (a part and a bone can share
	// a name: the kind of row matters too). False: none there.
	public bool DeletePickedKeysHere()
	{
		var picked = _state.Picked;
		if (picked.Count == 0) return false;
		var bones = picked.OfType<Bone>().Select(b => b.Name).ToHashSet();
		var parts = picked.OfType<Slot>().Select(s => s.Name).ToHashSet();
		var before = AnimationSnapshot.Capture(_state.Animation);
		bool removed = false;
		foreach (var tl in _state.Animation.Timelines)
			if ((RigOps.TargetsBone(tl) && bones.Contains(tl.Target)) || (PartOps.TargetsPart(tl) && parts.Contains(tl.Target)))
				removed |= tl.RemoveKey(_state.Time);
		if (!removed) return false;
		string what = picked.Count > 1 ? $"{picked.Count} picked" : picked[0] is Slot slot ? slot.Name : ((Bone)picked[0]).Name;
		_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
			$"Delete keys of {what} @ frame {_state.Frame}", before, AnimationSnapshot.Capture(_state.Animation), s => s.Restore()));
		return true;
	}
}
