using System.Numerics;

namespace Animator.Viewer;

// A bone's values in the rest pose, or keyed at the playhead
public enum BoneField { X, Y, Rotation, Length, ScaleX, ScaleY, Turn, Tilt, Follow, RangeX, RangeY }

// The open object's rig, edited (EditorSession's side of the Rig panel, Bind and the bone list):
// bones added, renamed, set up, keyed, connected, hung from others, deleted; look and IK
// constraints; parts put on bones; a drawn object given bones. Each edit is one undo step and
// says whether it changed anything. Angles are in radians.
public sealed partial class EditorSession
{
	// ======================= bones =======================

	// A new bone on the end of `parent` (or a root bone), 50 long, selected. Snap: glued to the
	// parent's end, staying there as it changes.
	public Bone AddBone(Bone? parent, bool snap)
	{
		Bone? made = null;
		State.EditStructure(() =>
		{
			var position = parent == null ? Vector2.Zero : new Vector2(parent.Length, 0);
			made = RigOps.AddBone(Rig, parent, "bone", position, 0f, 50f);
			if (parent != null && snap) made.Connected = true;
			State.Selected = made;
			State.SelectedSlot = null;
			return $"Add bone {made.Name}";
		});
		return made!;
	}

	// False: empty or another bone's name. Its keys follow, and objects hanging on it.
	public bool RenameBone(Bone bone, string newName)
	{
		newName = newName.Trim();
		if (newName == bone.Name) return false;
		string oldName = bone.Name;
		return EditStructureAndScene(() =>
		{
			if (!RigOps.RenameBone(Rig, State.Animations, bone, newName)) return null;
			if (Active != null) SceneOps.RenameHolderBone(Scene, Active, oldName, newName);
			return $"Rename bone to {newName}";
		});
	}

	// A rest value. Keep parts: placing, turning or stretching moves nothing else on screen
	// (FrozenRest); placing it frees it from its parent's end either way.
	public bool SetBoneRest(Bone bone, BoneField field, float v, bool keepParts)
	{
		var look = Rig.LookConstraints.Find(c => c.Bone == bone);
		return State.EditStructure(() =>
		{
			if (keepParts && field is BoneField.X or BoneField.Y or BoneField.Rotation or BoneField.Length)
			{
				if (field is BoneField.X or BoneField.Y) RigOps.SetConnected(bone, false);
				var frozen = FrozenRest.Capture(Rig);
				switch (field)
				{
					case BoneField.X or BoneField.Y:
						var local = field == BoneField.X ? new Vector2(v, bone.SetupPosition.Y) : new Vector2(bone.SetupPosition.X, v);
						RestEdits.MoveJoint(frozen, bone, bone.Parent is { } parent ? Vector2.Transform(local, parent.World) : local);
						break;
					case BoneField.Rotation: RestEdits.Turn(frozen, bone, v); break;
					default: RestEdits.Stretch(frozen, bone, v); break;
				}
				return $"Set {bone.Name} {field} (parts stay put)";
			}
			switch (field)
			{
				case BoneField.X:
					RigOps.SetConnected(bone, false);
					bone.SetupPosition = new Vector2(v, bone.SetupPosition.Y);
					break;
				case BoneField.Y:
					RigOps.SetConnected(bone, false);
					bone.SetupPosition = new Vector2(bone.SetupPosition.X, v);
					break;
				case BoneField.Rotation: bone.SetupRotation = v; break;
				case BoneField.Length: bone.Length = MathF.Max(0, v); break;
				case BoneField.ScaleX: bone.SetupScale = new Vector2(v, bone.SetupScale.Y); break;
				case BoneField.ScaleY: bone.SetupScale = new Vector2(bone.SetupScale.X, v); break;
				case BoneField.Turn: bone.SetupTurn = v; break;
				case BoneField.Tilt: bone.SetupTilt = v; break;
				case BoneField.Follow when look != null: look.Follow = v; break;
				case BoneField.RangeX when look != null: look.Radius = new Vector2(MathF.Max(0, v), look.Radius.Y); break;
				case BoneField.RangeY when look != null: look.Radius = new Vector2(look.Radius.X, MathF.Max(0, v)); break;
				default: return null;
			}
			return $"Set {bone.Name} {field}";
		});
	}

	// A pose value keyed at the playhead (on its frame), the bone's other values as posed now
	public bool KeyBone(Bone bone, BoneField field, float v)
	{
		State.Playing = false;
		State.SetFrame(State.Frame);
		var anim = State.Animation;
		float t = State.Time;
		int frame = State.Frame;
		static float Ratio(float a, float b) => MathF.Abs(b) < 1e-6f ? a : a / b;
		return State.EditStructure(() =>
		{
			switch (field)
			{
				case BoneField.X:
					anim.GetOrCreate(bone.Name, n => new TranslateTimeline(n)).Track.Add(t, new Vector2(v, bone.Position.Y) - bone.SetupPosition);
					break;
				case BoneField.Y:
					anim.GetOrCreate(bone.Name, n => new TranslateTimeline(n)).Track.Add(t, new Vector2(bone.Position.X, v) - bone.SetupPosition);
					break;
				case BoneField.Rotation:
					anim.GetOrCreate(bone.Name, n => new RotateTimeline(n)).Track.Add(t, v - bone.SetupRotation);
					break;
				case BoneField.ScaleX:
					anim.GetOrCreate(bone.Name, n => new ScaleTimeline(n))
						.Track.Add(t, new Vector2(Ratio(v, bone.SetupScale.X), Ratio(bone.Scale.Y, bone.SetupScale.Y)));
					break;
				case BoneField.ScaleY:
					anim.GetOrCreate(bone.Name, n => new ScaleTimeline(n))
						.Track.Add(t, new Vector2(Ratio(bone.Scale.X, bone.SetupScale.X), Ratio(v, bone.SetupScale.Y)));
					break;
				case BoneField.Turn:
					anim.GetOrCreate(bone.Name, n => new DepthTimeline(n)).Track.Add(t, new Vector2(bone.Tilt - bone.SetupTilt, v - bone.SetupTurn));
					break;
				case BoneField.Tilt:
					anim.GetOrCreate(bone.Name, n => new DepthTimeline(n)).Track.Add(t, new Vector2(v - bone.SetupTilt, bone.Turn - bone.SetupTurn));
					break;
				default:
					return null;
			}
			return $"Key {bone.Name} {field} @ frame {frame}";
		});
	}

	// Glued to its parent's end (true), or free
	public bool SetBoneConnected(Bone bone, bool connected) =>
		bone.Parent is { } parent && State.EditStructure(() => RigOps.SetConnected(bone, connected)
			? (connected ? $"Snap {bone.Name} to the end of {parent.Name}" : $"Free {bone.Name} from the end of {parent.Name}")
			: null);

	// Whether it turns and scales with its parent
	public bool SetBoneInherit(Bone bone, bool rotation, bool scale) =>
		State.EditStructure(() =>
		{
			if (bone.InheritRotation == rotation && bone.InheritScale == scale) return null;
			bone.InheritRotation = rotation;
			bone.InheritScale = scale;
			return $"Inheritance of {bone.Name}";
		});

	// Hangs a bone from another (null: a root bone). Keep parts: one on its parent's end lets go
	// first, so it and what's on it stay where they are; else it moves onto the new parent's end.
	// (Inside an undo step: if it can't, the step puts the joint back.)
	public bool HangBone(Bone bone, Bone? parent, bool keepParts)
	{
		if (keepParts && parent != bone.Parent) RigOps.SetConnected(bone, false);
		return RigOps.Reparent(Rig, bone, parent);
	}

	// The bone's parent chosen in the bone panel (label: how the choice reads), as one undo step
	public bool ParentBone(Bone bone, Bone? parent, string label, bool keepParts) =>
		State.EditStructure(() => HangBone(bone, parent, keepParts) ? $"Parent {bone.Name} to {label}" : null);

	// Several bones hung from one, as one undo step (how many were)
	public int HangBones(IReadOnlyList<Bone> bones, Bone? parent, bool keepParts)
	{
		int hung = 0;
		State.EditStructure(() =>
		{
			hung = bones.Count(b => HangBone(b, parent, keepParts));
			string onto = parent?.Name ?? "nothing (a root bone)";
			return hung == 0 ? null : bones.Count == 1 ? $"Hang {bones[0].Name} from {onto}" : $"Hang {hung} bones from {onto}";
		});
		return hung;
	}

	// Deleted, what was on it going to its parent and staying where it is (objects it carried too)
	public bool DissolveBone(Bone bone)
	{
		if (bone.Parent is not { } parent) return false;
		State.SelectedKeys.Clear();
		return EditStructureAndScene(() =>
		{
			if (Active != null) SceneOps.ReleaseFromBones(Scene, Active, new HashSet<string> { bone.Name });
			if (!RigOps.DissolveBone(Rig, State.Animations, bone)) return null;
			State.Selected = parent;
			return $"Delete bone {bone.Name} (what was on it went to {parent.Name})";
		});
	}

	// Deleted with everything on it (objects it carried let go, staying where they are)
	public bool DeleteBone(Bone bone)
	{
		var parent = bone.Parent;
		State.SelectedKeys.Clear();
		return EditStructureAndScene(() =>
		{
			var doomed = Rig.AllBones().Where(b => b == bone || b.IsDescendantOf(bone)).Select(b => b.Name).ToHashSet();
			if (Active != null) SceneOps.ReleaseFromBones(Scene, Active, doomed);
			RigOps.DeleteBone(Rig, State.Animations, bone);
			State.Selected = parent;
			return $"Delete bone {bone.Name}";
		});
	}

	// ======================= look and IK =======================

	public LookConstraint? LookOf(Bone? bone) => bone == null ? null : Rig.LookConstraints.Find(c => c.Bone == bone);

	// The bone looks at another (null: at nothing)
	public bool SetLookTarget(Bone bone, Bone? target) =>
		State.EditStructure(() => RigOps.SetLookTarget(Rig, bone, target)
			? (target == null ? $"Remove look from {bone.Name}" : $"{bone.Name} looks at {target.Name}")
			: null);

	public bool SetLookShape(Bone bone, LookShape shape) =>
		LookOf(bone) is { } look && look.Shape != shape && State.EditStructure(() =>
		{
			look.Shape = shape;
			return $"Look shape: {shape}";
		});

	// The IK constraint whose chain ends at this bone (where it's edited), and any it's part of
	public IkConstraint? IkEndingAt(Bone? bone) => bone == null ? null : Rig.IkConstraints.Find(c => c.Bones.Count > 0 && c.Bones[^1] == bone);
	public IkConstraint? IkContaining(Bone? bone) => bone == null ? null : Rig.IkConstraints.Find(c => c.Bones.Contains(bone));

	// An IK chain of this many bones ending at the bone (0: none). False: it can't be (some of
	// those bones are in another chain, or the target would move along with it).
	public bool SetIkLength(Bone bone, int length)
	{
		var ik = IkEndingAt(bone);
		if (length == (ik?.Bones.Count ?? 0)) return false;
		return State.EditStructure(() =>
		{
			if (ik == null) return RigOps.CreateIk(Rig, bone, length) is { } created ? $"Create IK {created.Name}" : null;
			if (length == 0)
			{
				RigOps.DeleteIk(Rig, State.Animations, ik);
				return $"Remove IK {ik.Name}";
			}
			return RigOps.SetIkLength(Rig, ik, length) ? $"IK {ik.Name}: {length} bones" : null;
		});
	}

	public bool SetIkTarget(IkConstraint ik, Bone target) =>
		State.EditStructure(() => RigOps.SetIkTarget(ik, target) ? $"{ik.Name} targets {target.Name}" : null);

	// On (fully) or off at rest, and which way it bends
	public bool SetIkOptions(IkConstraint ik, bool on, bool flip)
	{
		float mix = on ? 1f : 0f;
		int bend = flip ? -1 : 1;
		if (mix == ik.SetupMix && bend == ik.BendDirection) return false;
		return State.EditStructure(() =>
		{
			ik.SetupMix = mix;
			ik.BendDirection = bend;
			return $"{ik.Name} settings";
		});
	}

	// ======================= parts on bones =======================

	// Each part onto the bone nearest to most of it; how many moved
	public int PartsToNearestBones(IReadOnlyList<Slot> parts, string what)
	{
		int moved = 0;
		State.EditStructure(() =>
		{
			moved = PartRigging.PartsToNearestBones(Rig, Assets, parts);
			return moved > 0 ? $"Put {what} on the nearest bones" : null;
		});
		return moved;
	}

	// Parts put on a bone, staying where they are; how many moved
	public int PutPartsOnBone(IReadOnlyList<Slot> parts, Bone bone)
	{
		int moved = 0;
		State.EditStructure(() =>
		{
			moved = parts.Count(part => PartOps.MoveToBone(Rig, part, bone));
			return moved == 0 ? null : moved == 1 && parts.Count == 1 ? $"Put part {parts[0].Name} on {bone.Name}" : $"Put {moved} parts on {bone.Name}";
		});
		return moved;
	}

	// A drawn object made a rigged one (see PartRigging.GiveBones), a root bone this long (and,
	// suggested, bones from its parts' shapes); every part stays where it is. Null: it isn't a
	// drawn object open for editing; else how many bones were suggested.
	public int? GiveBones(float length, bool suggest)
	{
		if (Active is not { Kind: ObjectKind.Drawn } obj) return null;
		int suggested = 0;
		if (!EditStructureAndScene(() =>
		{
			suggested = PartRigging.GiveBones(obj, Assets, length, suggest);
			return suggest ? $"Give {obj.Name} suggested bones" : $"Give {obj.Name} a root bone";
		})) return null;
		State.Selected = Rig.Roots.FirstOrDefault();
		State.SelectedSlot = null;
		State.AnimateMode = false;
		return suggested;
	}
}
