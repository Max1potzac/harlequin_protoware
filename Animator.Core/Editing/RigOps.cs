using System.Numerics;

namespace Animator.Core.Editing;

// Structural rig edits. These change the model directly; the editor wraps them in undo steps.
public static class RigOps
{
	public static bool TargetsBone(Timeline t) =>
		t is RotateTimeline or TranslateTimeline or ScaleTimeline or DepthTimeline or SpaceTimeline;

	// Space keys also NAME bones (the bone holding this one), so renames and deletes must reach them
	private static void RewriteHolders(IEnumerable<Animation> animations, Func<string, string?> rewrite)
	{
		foreach (var space in animations.SelectMany(a => a.Timelines).OfType<SpaceTimeline>())
		{
			var keys = space.Track.Keys;
			for (int i = 0; i < keys.Count; i++)
			{
				string? holder = keys[i].Value;
				if (string.IsNullOrEmpty(holder)) continue;   // its own parent, or detached
				string? replacement = rewrite(holder);
				if (replacement != holder) keys[i] = keys[i] with { Value = replacement };
			}
		}
	}

	public static string UniqueBoneName(Skeleton skeleton, string baseName)
	{
		var used = skeleton.AllBones().Select(b => b.Name).ToHashSet();
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
		{
			string candidate = $"{baseName}{i}";
			if (!used.Contains(candidate)) return candidate;
		}
	}

	public static Bone AddBone(Skeleton skeleton, Bone? parent, string name,
							   Vector2 position, float rotation, float length)
	{
		var bone = new Bone
		{
			Name = UniqueBoneName(skeleton, name),
			Length = length,
			SetupPosition = position,
			SetupRotation = rotation
		};
		if (parent == null) skeleton.Roots.Add(bone);
		else parent.AddChild(bone);
		return bone;
	}

	public static Bone AddBoneBetween(Skeleton skeleton, Bone? parent, Vector2 headWorld, Vector2 tipWorld)
	{
		var dir = tipWorld - headWorld;
		float worldAngle = MathF.Atan2(dir.Y, dir.X);
		var position = parent == null ? headWorld : ToLocal(parent, headWorld);
		float rotation = worldAngle - (parent?.WorldRotation ?? 0f);
		return AddBone(skeleton, parent, "bone", position, rotation, dir.Length());
	}

	public static void DeleteBone(Skeleton skeleton, IEnumerable<Animation> animations, Bone bone)
	{
		var doomed = new HashSet<Bone> { bone };
		foreach (var b in skeleton.AllBones())
			if (b.IsDescendantOf(bone)) doomed.Add(b);
		var boneNames = doomed.Select(b => b.Name).ToHashSet();

		if (bone.Parent is { } parent) parent.RemoveChild(bone);
		else skeleton.Roots.Remove(bone);

		var slotNames = skeleton.Slots.Where(s => doomed.Contains(s.Bone)).Select(s => s.Name).ToHashSet();
		skeleton.Slots.RemoveAll(s => doomed.Contains(s.Bone));
		foreach (var skin in skeleton.AllSkins())
			foreach (var name in slotNames) skin.RemoveSlot(name);
		PartOps.RemoveUnusedAssets(skeleton);
		var ikNames = skeleton.IkConstraints
			.Where(c => doomed.Contains(c.Target) || c.Bones.Any(doomed.Contains))
			.Select(c => c.Name).ToHashSet();
		skeleton.IkConstraints.RemoveAll(c => ikNames.Contains(c.Name));

		skeleton.LookConstraints.RemoveAll(c => doomed.Contains(c.Bone) || doomed.Contains(c.Target));

		foreach (var anim in animations)
			anim.Timelines.RemoveAll(t =>
				(TargetsBone(t) && boneNames.Contains(t.Target)) ||
				(t is AttachmentTimeline && slotNames.Contains(t.Target)) ||
				(t is IkMixTimeline && ikNames.Contains(t.Target)));

		// Bones that were held by a deleted bone are let go (they follow nothing from then on)
		RewriteHolders(animations, holder => boneNames.Contains(holder) ? SpaceTimeline.World : holder);
	}

	public static bool RenameBone(Skeleton skeleton, IEnumerable<Animation> animations, Bone bone, string newName)
	{
		newName = newName.Trim();
		if (newName.Length == 0 || newName == bone.Name) return false;
		if (skeleton.AllBones().Any(b => b != bone && b.Name == newName)) return false;

		string oldName = bone.Name;
		bone.Name = newName;
		foreach (var anim in animations)
			foreach (var t in anim.Timelines)
				if (TargetsBone(t) && t.Target == oldName) t.Target = newName;
		RewriteHolders(animations, holder => holder == oldName ? newName : holder);
		return true;
	}

	public static bool Reparent(Skeleton skeleton, Bone bone, Bone? newParent)
	{
		if (newParent == bone.Parent) return false;
		if (newParent != null && (newParent == bone || newParent.IsDescendantOf(bone))) return false;

		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		var world = bone.World;
		var worldPos = world.Translation;
		float worldRot = bone.WorldRotation;
		var worldScale = ScaleOf(world);

		if (bone.Parent is { } oldParent) oldParent.RemoveChild(bone);
		else skeleton.Roots.Remove(bone);
		if (newParent == null) skeleton.Roots.Add(bone);
		else newParent.AddChild(bone);

		bone.SetupPosition = newParent == null ? worldPos : ToLocal(newParent, worldPos);
		bone.SetupRotation = worldRot - (newParent != null && bone.InheritRotation ? newParent.WorldRotation : 0f);

		// The measured scale includes the bone's own depth rotation; take that back out
		var parentScale = newParent != null && bone.InheritScale ? ScaleOf(newParent.World) : Vector2.One;
		var depth = new Vector2(
			MathF.Max(MathF.Abs(MathF.Cos(bone.SetupTilt)), 0.001f),
			MathF.Max(MathF.Abs(MathF.Cos(bone.SetupTurn)), 0.001f));
		bone.SetupScale = worldScale / parentScale / depth;
		return true;
	}

	// Makes `bone` look at `target`; null removes its look constraint.
	// false = nothing changed, or the target would chase itself.
	public static bool SetLookTarget(Skeleton skeleton, Bone bone, Bone? target)
	{
		var existing = skeleton.LookConstraints.Find(c => c.Bone == bone);

		if (target == null)
		{
			if (existing == null) return false;
			skeleton.LookConstraints.Remove(existing);
			return true;
		}

		if (target == bone || target.IsDescendantOf(bone)) return false;

		if (existing != null)
		{
			if (existing.Target == target) return false;
			existing.Target = target;
			return true;
		}

		skeleton.LookConstraints.Add(new LookConstraint { Name = $"{bone.Name}_look", Bone = bone, Target = target });
		return true;
	}
	// ======================= IK =======================

	public static string UniqueIkName(Skeleton skeleton, string baseName)
	{
		var used = skeleton.IkConstraints.Select(c => c.Name).ToHashSet();
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
		{
			string candidate = $"{baseName}{i}";
			if (!used.Contains(candidate)) return candidate;
		}
	}

	// `length` bones ending at `end`, root-most first. null if the hierarchy isn't that deep.
	public static List<Bone>? ChainEndingAt(Bone end, int length)
	{
		var chain = new List<Bone>();
		for (var b = end; b != null && chain.Count < length; b = b.Parent)
			chain.Insert(0, b);
		return chain.Count == length ? chain : null;
	}

	// A target that moves along with the chain would chase itself forever
	private static bool UsableAsTarget(IReadOnlyList<Bone> chain, Bone target) =>
		!chain.Contains(target) && !chain.Any(b => target.IsDescendantOf(b));

	private static bool InOtherIk(Skeleton skeleton, IEnumerable<Bone> chain, IkConstraint? except) =>
		chain.Any(b => skeleton.IkConstraints.Any(c => c != except && c.Bones.Contains(b)));

	// A new IK chain ending at `end`. Without a target, a new root bone at the tip becomes one.
	public static IkConstraint? CreateIk(Skeleton skeleton, Bone end, int length, Bone? target = null)
	{
		var chain = ChainEndingAt(end, length);
		if (chain == null || InOtherIk(skeleton, chain, null)) return null;
		if (target != null && !UsableAsTarget(chain, target)) return null;

		if (target == null)
		{
			skeleton.SetToSetupPose();
			skeleton.UpdateWorld();   // rest pose, so the target lands exactly on the tip
			target = AddBone(skeleton, null, $"{end.Name}_target", end.TipWorld, 0f, 10f);
		}

		var ik = new IkConstraint { Name = UniqueIkName(skeleton, $"{end.Name}_ik"), Target = target };
		ik.Bones.AddRange(chain);
		skeleton.IkConstraints.Add(ik);
		return ik;
	}

	public static bool SetIkLength(Skeleton skeleton, IkConstraint ik, int length)
	{
		var chain = ChainEndingAt(ik.Bones[^1], length);
		if (chain == null || chain.Count == ik.Bones.Count) return false;
		if (InOtherIk(skeleton, chain, ik) || !UsableAsTarget(chain, ik.Target)) return false;
		ik.Bones.Clear();
		ik.Bones.AddRange(chain);
		return true;
	}

	public static bool SetIkTarget(IkConstraint ik, Bone target)
	{
		if (ik.Target == target || !UsableAsTarget(ik.Bones, target)) return false;
		ik.Target = target;
		return true;
	}

	public static void DeleteIk(Skeleton skeleton, IEnumerable<Animation> animations, IkConstraint ik)
	{
		skeleton.IkConstraints.Remove(ik);
		foreach (var anim in animations)
			anim.Timelines.RemoveAll(t => t is IkMixTimeline && t.Target == ik.Name);
	}
	private static Vector2 ToLocal(Bone parent, Vector2 world) =>
		Matrix3x2.Invert(parent.World, out var inverse) ? Vector2.Transform(world, inverse) : world;

	private static Vector2 ScaleOf(Matrix3x2 m) =>
		new(new Vector2(m.M11, m.M12).Length(), new Vector2(m.M21, m.M22).Length());
}