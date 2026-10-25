using Animator.Core.Assets;

namespace Animator.Core.Rigging;

public class Skeleton
{
    public List<Bone> Roots { get; } = new();
    public List<Slot> Slots { get; } = new();          // list order = the setup draw order (back to front)
    public Dictionary<Guid, Asset> Assets { get; } = new();

    public Skin DefaultSkin { get; } = new() { Name = "default" };
    public Skin? ActiveSkin { get; set; }

	// Named groups of bones and parts, to pick in one click (the sets in the rig panel)
	public List<SelectionSet> Sets { get; } = new();
    public Bone? FindBone(string name)
    {
        foreach (var root in Roots)
        {
            var found = FindBone(root, name);
            if (found != null) return found;
        }
        return null;
    }

	// Every attachment in the default and active skins
	public IEnumerable<Attachment> AllAttachments()
	{
		var skins = new List<Skin> { DefaultSkin };
		if (ActiveSkin != null && ActiveSkin != DefaultSkin) skins.Add(ActiveSkin);
		return skins.SelectMany(s => s.Attachments).Distinct();
	}
	private static Bone? FindBone(Bone bone, string name)
    {
        if (bone.Name == name) return bone;
        foreach (var child in bone.Children)
        {
            var found = FindBone(child, name);
            if (found != null) return found;
        }
        return null;
    }
    public IEnumerable<Bone> AllBones()
    {
        foreach (var root in Roots)
            foreach (var b in Flatten(root))
                yield return b;
    }

    private static IEnumerable<Bone> Flatten(Bone bone)
    {
        yield return bone;
        foreach (var child in bone.Children)
            foreach (var b in Flatten(child))
                yield return b;
    }
    public Slot? FindSlot(string name) => Slots.Find(s => s.Name == name);

	// The order the parts are drawn in right now, back to front: the setup order (Slots'), unless
	// an animation's draw order keys say otherwise (kept until the pose is reset)
	private List<Slot>? _drawOrder;
	public IReadOnlyList<Slot> DrawOrder => _drawOrder is { } order && order.Count == Slots.Count ? order : Slots;

	// Back to the setup order (until a draw order key sets another)
	public void ResetDrawOrder() => _drawOrder = null;

	// Draws the parts in this order (back to front, by name). A part it doesn't name (one added
	// since) goes just in front of the part it's in front of in the setup order.
	public void SetDrawOrder(IReadOnlyList<string> names)
	{
		var bySlot = new Dictionary<string, Slot>(StringComparer.Ordinal);
		foreach (var slot in Slots) bySlot.TryAdd(slot.Name, slot);
		var order = new List<Slot>(Slots.Count);
		var placed = new HashSet<Slot>();
		foreach (string name in names)
			if (bySlot.TryGetValue(name, out var slot) && placed.Add(slot)) order.Add(slot);
		for (int i = 0; i < Slots.Count && placed.Count < Slots.Count; i++)
		{
			if (placed.Contains(Slots[i])) continue;
			int at = 0;
			for (int j = i - 1; j >= 0; j--)
				if (placed.Contains(Slots[j]))
				{
					at = order.IndexOf(Slots[j]) + 1;
					break;
				}
			order.Insert(at, Slots[i]);
			placed.Add(Slots[i]);
		}
		_drawOrder = order.SequenceEqual(Slots) ? null : order;
	}
    public void ApplyLimits()
    {
        foreach (var root in Roots) ApplyLimits(root);
    }

    private static void ApplyLimits(Bone bone)
    {
        bone.ApplyLimits();
        foreach (var child in bone.Children) ApplyLimits(child);
    }

	public void SetToSetupPose()
	{
		foreach (var root in Roots) ResetBone(root);
		foreach (var slot in Slots) slot.ResetToSetup();
		_drawOrder = null;
		foreach (var c in IkConstraints) c.Mix = c.SetupMix;
		foreach (var c in LookConstraints) c.Mix = c.SetupMix;
	}

	private static void ResetBone(Bone bone)
    {
        bone.ResetToSetup();
        foreach (var child in bone.Children) ResetBone(child);
    }
    public List<IkConstraint> IkConstraints { get; } = new();

    public IkConstraint? FindIkConstraint(string name) =>
        IkConstraints.Find(c => c.Name == name);
	public List<LookConstraint> LookConstraints { get; } = new();
	// The full pipeline in one call: limits → world transforms → IK → look constraints.
	public void UpdatePose()
	{
		ApplyLimits();
		UpdateWorld();
		foreach (var c in IkConstraints) c.Solve();
		foreach (var c in LookConstraints) c.Solve();   // after IK: targets may sit on IK-driven bones
		UpdateHeldBones();   // a bone held by an IK hand must follow where IK put the hand
	}

	// Takes over another skeleton's contents. Everything that holds a reference to
	// THIS skeleton (renderer, tools, timeline, paint editor) keeps working.
	public void ReplaceWith(Skeleton other)
	{
		Roots.Clear();
		Roots.AddRange(other.Roots);
		Slots.Clear();
		Slots.AddRange(other.Slots);
		_drawOrder = null;
		Assets.Clear();
		foreach (var (id, asset) in other.Assets) Assets[id] = asset;
		IkConstraints.Clear();
		IkConstraints.AddRange(other.IkConstraints);
		LookConstraints.Clear();
		LookConstraints.AddRange(other.LookConstraints);
		DefaultSkin.ReplaceWith(other.DefaultSkin);
		ActiveSkin = other.ActiveSkin == other.DefaultSkin ? DefaultSkin : other.ActiveSkin;
	}

	public void Clear() => ReplaceWith(new Skeleton());

	public void UpdateWorld()
    {
        foreach (var root in Roots) root.UpdateWorld();
        UpdateHeldBones();
    }

	// A bone held by another bone (an attach key) is built on that bone's world transform,
	// but the holder may come later in the hierarchy. So held bones get recomputed after
	// everything else; a few passes also settle chains (a sword in a hand held by... a hand).
	private void UpdateHeldBones()
	{
		var held = AllBones().Where(b => b.SpaceOverridden && b.SpaceParent != null).ToList();
		for (int pass = 0; pass < 3 && held.Count > 0; pass++)
			foreach (var bone in held) bone.UpdateWorld();
	}

    // Active skin wins; falls back to default. This is what makes
    // "outfit swaps" work without duplicating the whole rig.
    public Attachment? ResolveAttachment(Slot slot)
    {
        if (slot.ActiveAttachment == null) return null;
        return ActiveSkin?.Get(slot.Name, slot.ActiveAttachment)
            ?? DefaultSkin.Get(slot.Name, slot.ActiveAttachment);
    }
	public IEnumerable<Skin> AllSkins()
	{
		yield return DefaultSkin;
		if (ActiveSkin != null && ActiveSkin != DefaultSkin) yield return ActiveSkin;
	}
}