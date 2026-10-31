using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// Clipping masks (the Layers view's right-click menu, and the part panel): a part used as a mask
// clips the parts drawn above it, up to a last one (or all of them): they show only where the
// mask's picture does (its see-through pixels hide them). The mask itself isn't drawn, unless
// it's set to show too (like Photoshop's clipping, where the base shows). Masks don't nest: a
// mask among the clipped parts is drawn as an ordinary part. Like the other ops, these change
// the model directly; the editor adds undo.
public static class MaskOps
{
	// A part made a mask for the parts above it (until: the last one it clips; null = all)
	public static void MakeMask(Skeleton skeleton, Slot mask, Slot? until = null, bool shows = false)
	{
		mask.IsMask = true;
		mask.MaskUntil = until;
		mask.MaskShows = shows;
	}

	public static void ClearMask(Slot mask)
	{
		mask.IsMask = false;
		mask.MaskUntil = null;
		mask.MaskShows = false;
	}

	// The parts a mask clips, in this order (the draw order as it stands: an animation can key it):
	// those right above it, up to its last one
	public static List<Slot> Clipped(IReadOnlyList<Slot> order, Slot mask)
	{
		var list = new List<Slot>();
		int at = IndexOf(order, mask);
		if (at < 0 || !mask.IsMask) return list;
		int end = mask.MaskUntil is { } until ? IndexOf(order, until) : order.Count - 1;
		for (int i = at + 1; i <= end && i < order.Count; i++) list.Add(order[i]);
		return list;
	}

	public static List<Slot> Clipped(Skeleton skeleton, Slot mask) => Clipped(skeleton.DrawOrder, mask);

	// The mask clipping a part (null: none)
	public static Slot? MaskOf(Skeleton skeleton, Slot part)
	{
		var order = skeleton.DrawOrder;
		foreach (var mask in order)
			if (mask.IsMask && Clipped(order, mask).Contains(part)) return mask;
		return null;
	}

	// A part going away: masks it ended at end at the part below it instead
	public static void Forget(Skeleton skeleton, Slot gone)
	{
		foreach (var mask in skeleton.Slots.Where(s => s.MaskUntil == gone))
		{
			int at = skeleton.Slots.IndexOf(gone);
			var below = at > 0 ? skeleton.Slots[at - 1] : null;
			mask.MaskUntil = below == null || below == mask || skeleton.Slots.IndexOf(below) < skeleton.Slots.IndexOf(mask) ? mask : below;
		}
	}

	private static int IndexOf(IReadOnlyList<Slot> order, Slot slot)
	{
		for (int i = 0; i < order.Count; i++)
			if (order[i] == slot) return i;
		return -1;
	}
}
