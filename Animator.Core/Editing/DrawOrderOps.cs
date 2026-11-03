namespace Animator.Core.Editing;

// The order parts are drawn in, the way layers stack in any editor: whatever bones they're on.
// In the setup (the skeleton's own order), or keyed in an animation from a time on. Orders run
// back to front, like the skeleton's.
public static class DrawOrderOps
{
	// The order with `moving` (keeping their own order) taken out and put back in at `index`,
	// counted among the others: 0 = at the back, the others' count = at the front
	public static List<Slot> Moved(IReadOnlyList<Slot> order, IReadOnlyCollection<Slot> moving, int index)
	{
		var kept = order.Where(s => !moving.Contains(s)).ToList();
		var moved = order.Where(moving.Contains).ToList();
		kept.InsertRange(Math.Clamp(index, 0, kept.Count), moved);
		return kept;
	}

	// One step toward the front (+1) or the back (-1): each part passes the nearest part it's
	// not moving with (parts moved together keep their order, and stop at the end). `shows`: the
	// parts that show now; a step passes the nearest of those, and the hidden ones on the way (a
	// step past a hidden part changes nothing to see). With none showing that way, the nearest.
	public static List<Slot> Stepped(IReadOnlyList<Slot> order, IReadOnlyCollection<Slot> moving, int direction, Func<Slot, bool>? shows = null)
	{
		var result = order.ToList();
		int step = Math.Sign(direction);
		if (step == 0) return result;
		var parts = result.Where(moving.Contains).ToList();
		if (step > 0) parts.Reverse();
		foreach (var part in parts)
		{
			int i = result.IndexOf(part), j = -1;
			for (int k = i + step; k >= 0 && k < result.Count && !moving.Contains(result[k]); k += step)
			{
				if (j < 0) j = k;   // (the nearest, if none shows)
				if (shows == null || shows(result[k])) { j = k; break; }
			}
			if (j < 0) continue;
			result.RemoveAt(i);
			result.Insert(j, part);
		}
		return result;
	}

	// What a change of order did: each moved part, each part it passed, and whether it's in front
	// of that one now
	public static List<(string Moved, string Other, bool InFront)> Passed(IReadOnlyList<Slot> before, IReadOnlyList<Slot> after, IReadOnlyCollection<Slot> moving)
	{
		var was = before.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
		var now = after.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
		var result = new List<(string, string, bool)>();
		foreach (var m in moving)
			foreach (var o in before)
				if (!moving.Contains(o) && was.ContainsKey(m) && now.ContainsKey(m) && now.ContainsKey(o)
					&& (was[m] < was[o]) != (now[m] < now[o]))
					result.Add((m.Name, o.Name, now[m] > now[o]));
		return result;
	}

	// An order (a key's) with those passes made again. Agrees: each moved part sat on the same
	// side of the parts it passed there as where it was passed (false: it didn't, a key drawing
	// them otherwise on purpose). Order: the new one (the same when there was nothing to do).
	public static (bool Agrees, string[] Order) Carry(IReadOnlyList<string> order, IReadOnlyList<(string Moved, string Other, bool InFront)> passed)
	{
		var list = order.ToList();
		foreach (var group in passed.GroupBy(p => p.Moved))
		{
			if (!list.Contains(group.Key)) continue;
			var others = group.Where(p => list.Contains(p.Other)).ToList();
			if (others.Count == 0) continue;
			int m = list.IndexOf(group.Key);
			if (others.Any(p => (list.IndexOf(p.Other) > m) != p.InFront)) return (false, order.ToArray());
			list.RemoveAt(m);
			if (others[0].InFront)
				list.Insert(others.Max(p => list.IndexOf(p.Other)) + 1, group.Key);
			else
				list.Insert(others.Min(p => list.IndexOf(p.Other)), group.Key);
		}
		return (true, list.ToArray());
	}

	// The keys from `after` on (or all of them) that agree with how the parts sat before a change
	// get the change too (see Carry). Stops at the first key that doesn't (unless `all`: every key
	// is looked at). True when a key changed.
	public static bool CarryToKeys(Animation animation, IReadOnlyList<(string Moved, string Other, bool InFront)> passed, float after, bool all)
	{
		if (passed.Count == 0 || animation.DrawOrder is not { } timeline) return false;
		bool changed = false;
		var keys = timeline.Track.Keys;
		for (int i = 0; i < keys.Count; i++)
		{
			if (!all && keys[i].Time <= after + 1e-4f) continue;
			var (agrees, order) = Carry(keys[i].Value, passed);
			if (!agrees)
			{
				if (all) continue;
				break;
			}
			if (order.SequenceEqual(keys[i].Value)) continue;
			keys[i] = keys[i] with { Value = order };
			changed = true;
		}
		return changed;
	}

	// All the way to the front or the back
	public static List<Slot> ToEnd(IReadOnlyList<Slot> order, IReadOnlyCollection<Slot> moving, bool front) =>
		Moved(order, moving, front ? order.Count : 0);

	// The skeleton's own (setup) order becomes this one: the same parts, rearranged
	public static bool SetSetupOrder(Skeleton skeleton, IReadOnlyList<Slot> order)
	{
		if (order.Count != skeleton.Slots.Count || order.SequenceEqual(skeleton.Slots)) return false;
		var parts = skeleton.Slots.ToHashSet();
		if (!order.All(parts.Remove)) return false;   // not the same parts (or one twice)
		skeleton.Slots.Clear();
		skeleton.Slots.AddRange(order);
		return true;
	}

	// The animation draws the parts in this order from `time` on: a key there (replacing one
	// there). False when it would already draw them so then, from a key there or one before.
	public static bool Key(Animation animation, float time, IReadOnlyList<Slot> order, IReadOnlyList<Slot> setup)
	{
		var names = order.Select(s => s.Name).ToArray();
		var timeline = animation.DrawOrder;
		string[]? before = null;
		if (timeline != null)
			foreach (var key in timeline.Track.Keys)
				if (key.Time <= time + 1e-4f) before = key.Value;
		before ??= setup.Select(s => s.Name).ToArray();
		if (before.SequenceEqual(names)) return false;
		(timeline ?? animation.GetOrCreateDrawOrder()).Add(time, names);
		return true;
	}

	// A part renamed: the keys say its new name
	public static void Rename(IEnumerable<Animation> animations, string oldName, string newName) =>
		Rewrite(animations, name => name == oldName ? newName : name);

	// Parts deleted: the keys forget them
	public static void Forget(IEnumerable<Animation> animations, IReadOnlySet<string> names) =>
		Rewrite(animations, name => names.Contains(name) ? null : name);

	private static void Rewrite(IEnumerable<Animation> animations, Func<string, string?> map)
	{
		foreach (var animation in animations)
		{
			if (animation.DrawOrder is not { } timeline) continue;
			var keys = timeline.Track.Keys;
			for (int i = 0; i < keys.Count; i++)
			{
				var names = keys[i].Value.Select(map).OfType<string>().ToArray();
				if (!names.SequenceEqual(keys[i].Value)) keys[i] = keys[i] with { Value = names };
			}
		}
	}
}
