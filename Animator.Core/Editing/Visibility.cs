using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// Getting bones and parts out of the way while editing (the eyes in the rig panel; H, Shift+H,
// Alt+H): a hidden bone hides what's on it and under it. Exports draw everything all the same.
public static class VisibilityOps
{
	// Hides (or shows) these bones and parts. false: they all already were.
	public static bool SetHidden(IEnumerable<object> items, bool hidden)
	{
		bool changed = false;
		foreach (var item in items)
		{
			switch (item)
			{
				case Bone bone when bone.Hidden != hidden:
					bone.Hidden = hidden;
					changed = true;
					break;
				case Slot slot when slot.Hidden != hidden:
					slot.Hidden = hidden;
					changed = true;
					break;
			}
		}
		return changed;
	}

	// Everything but these, hidden. A kept bone keeps what's on it and under it; a kept part keeps
	// just itself. What they hang from stays shown (it has to, for them to show), but not the
	// other parts on it.
	public static bool HideOthers(Skeleton skeleton, IEnumerable<object> keep)
	{
		var kept = keep.ToList();
		var keptBones = kept.OfType<Bone>().ToHashSet();
		var keptSlots = kept.OfType<Slot>().ToHashSet();

		// Bones that must show for the kept things to: the kept bones, the bones the kept parts are
		// on, and every bone above them
		var needed = new HashSet<Bone>();
		foreach (var bone in keptBones.Concat(keptSlots.Select(s => s.Bone)))
			for (var b = bone; b != null && needed.Add(b); b = b.Parent) { }

		bool changed = SetHidden(needed.Cast<object>().Concat(keptSlots), false);
		foreach (var bone in skeleton.AllBones())
		{
			if (needed.Contains(bone) || InKept(bone, keptBones)) continue;
			// Only the top of each hidden branch: what's under it goes with it (and comes back with it)
			if (bone.Parent == null || needed.Contains(bone.Parent)) changed |= SetHidden(new[] { bone }, true);
		}
		foreach (var slot in skeleton.Slots)
			if (!keptSlots.Contains(slot) && needed.Contains(slot.Bone) && !InKept(slot.Bone, keptBones))
				changed |= SetHidden(new[] { slot }, true);
		return changed;
	}

	// Everything shown again
	public static bool ShowAll(Skeleton skeleton) =>
		SetHidden(skeleton.AllBones().Cast<object>().Concat(skeleton.Slots), false);

	// On or under a kept bone
	private static bool InKept(Bone bone, HashSet<Bone> kept)
	{
		for (var b = bone; b != null; b = b.Parent)
			if (kept.Contains(b)) return true;
		return false;
	}
}

// What's hidden, locked and coloured, for undo
public sealed class EditAidsSnapshot
{
	private readonly List<(Bone Bone, bool Hidden, bool Locked, System.Numerics.Vector3? Color)> _bones;
	private readonly List<(Slot Slot, bool Hidden, bool Locked)> _slots;

	private EditAidsSnapshot(Skeleton skeleton)
	{
		_bones = skeleton.AllBones().Select(b => (b, b.Hidden, b.Locked, b.EditorColor)).ToList();
		_slots = skeleton.Slots.Select(s => (s, s.Hidden, s.Locked)).ToList();
	}

	public static EditAidsSnapshot Capture(Skeleton skeleton) => new(skeleton);

	public void Restore()
	{
		foreach (var (bone, hidden, locked, color) in _bones)
		{
			bone.Hidden = hidden;
			bone.Locked = locked;
			bone.EditorColor = color;
		}
		foreach (var (slot, hidden, locked) in _slots)
		{
			slot.Hidden = hidden;
			slot.Locked = locked;
		}
	}
}

// Locking bones and parts while editing (the locks in the rig panel; L, Shift+L, Alt+L): a locked
// one can't be picked or dragged in the viewport. Unlike hiding, only that one: what's on a locked
// bone and under it isn't locked.
public static class LockOps
{
	public static bool IsLocked(object item) => item switch { Bone b => b.Locked, Slot s => s.Locked, _ => false };

	// Locks (or unlocks) these. false: they all already were.
	public static bool SetLocked(IEnumerable<object> items, bool locked)
	{
		bool changed = false;
		foreach (var item in items)
		{
			switch (item)
			{
				case Bone bone when bone.Locked != locked:
					bone.Locked = locked;
					changed = true;
					break;
				case Slot slot when slot.Locked != locked:
					slot.Locked = locked;
					changed = true;
					break;
			}
		}
		return changed;
	}

	// Every bone and part but these, locked
	public static bool LockOthers(Skeleton skeleton, IEnumerable<object> keep)
	{
		var kept = keep.ToHashSet();
		bool changed = SetLocked(kept, false);
		changed |= SetLocked(skeleton.AllBones().Cast<object>().Concat(skeleton.Slots).Where(i => !kept.Contains(i)), true);
		return changed;
	}

	public static bool UnlockAll(Skeleton skeleton) =>
		SetLocked(skeleton.AllBones().Cast<object>().Concat(skeleton.Slots), false);
}

// Selection sets, for undo: which sets there are, their names and members
public sealed class SetsSnapshot
{
	private readonly Skeleton _skeleton;
	private readonly List<(SelectionSet Set, string Name, List<Bone> Bones, List<Slot> Slots)> _sets;

	private SetsSnapshot(Skeleton skeleton)
	{
		_skeleton = skeleton;
		_sets = skeleton.Sets.Select(s => (s, s.Name, s.Bones.ToList(), s.Slots.ToList())).ToList();
	}

	public static SetsSnapshot Capture(Skeleton skeleton) => new(skeleton);

	public void Restore()
	{
		_skeleton.Sets.Clear();
		foreach (var (set, name, bones, slots) in _sets)
		{
			set.Name = name;
			set.Bones.Clear();
			set.Bones.AddRange(bones);
			set.Slots.Clear();
			set.Slots.AddRange(slots);
			_skeleton.Sets.Add(set);
		}
	}
}
