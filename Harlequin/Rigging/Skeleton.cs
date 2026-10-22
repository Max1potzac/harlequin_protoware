using Animator.Core.Assets;

namespace Animator.Core.Rigging;

public class Skeleton
{
    public List<Bone> Roots { get; } = new();
    public List<Slot> Slots { get; } = new();          // list order = draw order
    public Dictionary<Guid, Asset> Assets { get; } = new();

    public Skin DefaultSkin { get; } = new() { Name = "default" };
    public Skin? ActiveSkin { get; set; }
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
        foreach (var slot in Slots) slot.ActiveAttachment = slot.SetupAttachment;
        foreach (var c in IkConstraints) c.Mix = c.SetupMix;
    }

    private static void ResetBone(Bone bone)
    {
        bone.ResetToSetup();
        foreach (var child in bone.Children) ResetBone(child);
    }
    public List<IkConstraint> IkConstraints { get; } = new();

    public IkConstraint? FindIkConstraint(string name) =>
        IkConstraints.Find(c => c.Name == name);

    // The full pipeline in one call: limits → world transforms → IK.
    public void UpdatePose()
    {
        ApplyLimits();
        UpdateWorld();                                  // FK pose first
        foreach (var c in IkConstraints) c.Solve();     // list order = solve order
    }
	// Takes over another skeleton's contents. Everything that holds a reference to
	// THIS skeleton (renderer, tools, timeline, paint editor) keeps working.
	public void ReplaceWith(Skeleton other)
	{
		Roots.Clear();
		Roots.AddRange(other.Roots);
		Slots.Clear();
		Slots.AddRange(other.Slots);
		Assets.Clear();
		foreach (var (id, asset) in other.Assets) Assets[id] = asset;
		IkConstraints.Clear();
		IkConstraints.AddRange(other.IkConstraints);
		DefaultSkin.ReplaceWith(other.DefaultSkin);
		ActiveSkin = other.ActiveSkin == other.DefaultSkin ? DefaultSkin : other.ActiveSkin;
	}

	public void Clear() => ReplaceWith(new Skeleton());

	public void UpdateWorld()
    {
        foreach (var root in Roots) root.UpdateWorld();
    }

    // Active skin wins; falls back to default. This is what makes
    // "outfit swaps" work without duplicating the whole rig.
    public Attachment? ResolveAttachment(Slot slot)
    {
        if (slot.ActiveAttachment == null) return null;
        return ActiveSkin?.Get(slot.Name, slot.ActiveAttachment)
            ?? DefaultSkin.Get(slot.Name, slot.ActiveAttachment);
    }
}