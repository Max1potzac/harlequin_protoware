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
	// not moving with (parts moved together keep their order, and stop at the end)
	public static List<Slot> Stepped(IReadOnlyList<Slot> order, IReadOnlyCollection<Slot> moving, int direction)
	{
		var result = order.ToList();
		var indexes = Enumerable.Range(0, result.Count).Where(i => moving.Contains(result[i])).ToList();
		if (direction > 0) indexes.Reverse();
		int limit = direction > 0 ? result.Count - 1 : 0;
		foreach (int start in indexes)
		{
			int i = start, j = i + Math.Sign(direction);
			if (i == limit || j < 0 || j >= result.Count || moving.Contains(result[j])) continue;
			(result[i], result[j]) = (result[j], result[i]);
		}
		return result;
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
