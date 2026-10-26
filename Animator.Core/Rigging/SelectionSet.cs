namespace Animator.Core.Rigging;

// A named group of bones and parts, picked in one click (the sets in the rig panel), like Blender's
// selection sets. It holds the bones and parts themselves, so renaming them doesn't matter; ones
// deleted since are just left out (and come back with an undo).
public sealed class SelectionSet
{
	public string Name { get; set; } = "";
	public List<Bone> Bones { get; } = new();
	public List<Slot> Slots { get; } = new();

	// The members still in this rig
	public IEnumerable<object> MembersIn(Skeleton skeleton)
	{
		var bones = skeleton.AllBones().ToHashSet();
		var slots = skeleton.Slots.ToHashSet();
		foreach (var bone in Bones)
			if (bones.Contains(bone)) yield return bone;
		foreach (var slot in Slots)
			if (slots.Contains(slot)) yield return slot;
	}
}
