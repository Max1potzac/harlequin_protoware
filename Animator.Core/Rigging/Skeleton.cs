using Animator.Core.Assets;

namespace Animator.Core.Rigging;

public class Skeleton
{
    public List<Bone> Roots { get; } = new();
    public List<Slot> Slots { get; } = new();          // list order = the setup draw order (back to front)
    public Dictionary<Guid, Asset> Assets { get; } = new();

    public Skin DefaultSkin { get; } = new() { Name = "default" };

	// The object's skins besides its own drawings (the default skin), in list order, and the ones
	// on now. Where two that are on have a drawing for the same part, the overlay wins, then the
	// one later in the list (see SkinOps for turning them on: one per category).
	public List<Skin> Skins { get; } = new();
	public HashSet<Skin> ShownSkins { get; } = new();
	public Skin? FindSkin(string name) => Skins.Find(s => s.Name == name);

	// The skins that are on, the one that wins first
	public IEnumerable<Skin> SkinsOnTop() =>
		Skins.Where(ShownSkins.Contains).Reverse().OrderBy(s => s.Overlay ? 0 : 1);

	// The drawing a part shows under this name: from the skins that are on, or its own
	public Attachment? FindAttachment(string slot, string name)
	{
		foreach (var skin in SkinsOnTop())
			if (skin.Get(slot, name) is { } drawing) return drawing;
		return DefaultSkin.Get(slot, name);
	}

	// Named groups of bones and parts, to pick in one click (the sets in the rig panel)
	public List<SelectionSet> Sets { get; } = new();

	// Colour layers on the whole object, applied first to last (see ColorLayer)
	public List<ColorLayer> ColorLayers { get; } = new();
	public ColorLayer? FindColorLayer(string name) => ColorLayers.Find(l => l.Name == name);

	// The skin a colour layer is in (null: none, it's the object's own)
	public Skin? SkinOf(ColorLayer layer) => Skins.Find(s => s.ColorLayers.Contains(layer));

	// Whether a colour layer applies now: on, and its skin (if it's in one) on
	public bool Applies(ColorLayer layer) => layer.Visible && (SkinOf(layer) is not { } skin || ShownSkins.Contains(skin));

	// Hitboxes and points on bones, for the game (the Boxes view; see HitboxOps)
	public List<Hitbox> Hitboxes { get; } = new();
	public List<RigPoint> Points { get; } = new();
	public Hitbox? FindHitbox(string name) => Hitboxes.Find(h => h.Name == name);
	public RigPoint? FindPoint(string name) => Points.Find(p => p.Name == name);

	// Poses kept by name, to put the rig that way again (the Poses view; see PoseOps)
	public List<Editing.SavedPose> Poses { get; } = new();
    public Bone? FindBone(string name)
    {
        foreach (var root in Roots)
        {
            var found = FindBone(root, name);
            if (found != null) return found;
        }
        return null;
    }

	// Every attachment in every skin (on or not)
	public IEnumerable<Attachment> AllAttachments() =>
		AllSkins().SelectMany(s => s.Attachments).Distinct();
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
		foreach (var hitbox in Hitboxes) hitbox.Active = hitbox.SetupActive;
		foreach (var c in LookConstraints) c.Mix = c.SetupMix;
		foreach (var layer in ColorLayers) layer.Adjust.Now = layer.Adjust.Setup;
		foreach (var asset in Assets.Values)   // colour layers in drawings: their picture is made again when they change
		{
			bool changed = false;
			foreach (var layer in asset.Layers)
				if (layer.Adjust is { } adjust && adjust.Now != adjust.Setup)
				{
					adjust.Now = adjust.Setup;
					changed = true;
				}
			if (changed) asset.MarkEdited();
		}
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
		Sets.Clear();
		Sets.AddRange(other.Sets);
		ColorLayers.Clear();
		ColorLayers.AddRange(other.ColorLayers);
		Poses.Clear();
		Poses.AddRange(other.Poses);
		Hitboxes.Clear();
		Hitboxes.AddRange(other.Hitboxes);
		Points.Clear();
		Points.AddRange(other.Points);
		DefaultSkin.ReplaceWith(other.DefaultSkin);
		Skins.Clear();
		Skins.AddRange(other.Skins);
		ShownSkins.Clear();
		ShownSkins.UnionWith(other.ShownSkins);
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

    // The skins that are on win; falls back to the object's own drawings. This is what makes
    // "outfit swaps" work without duplicating the whole rig.
    public Attachment? ResolveAttachment(Slot slot) =>
		slot.ActiveAttachment == null ? null : FindAttachment(slot.Name, slot.ActiveAttachment);

	// Every skin, on or not: the object's own drawings first
	public IEnumerable<Skin> AllSkins()
	{
		yield return DefaultSkin;
		foreach (var skin in Skins) yield return skin;
	}
}