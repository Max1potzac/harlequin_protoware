using System.Numerics;

namespace Animator.Core.Editing;

// The whole rig: hierarchy, rest poses, parts, skins, constraints, assets and animations.
// Restores the SAME objects instead of copies, so other undo steps keep pointing at the right things.
public sealed class StructureSnapshot
{
	private sealed record BoneState(
		Bone Bone, Bone? Parent, List<Bone> Children, string Name, float Length,
		Vector2 Position, float Rotation, Vector2 Scale, float Turn, float Tilt,
		bool InheritRotation, bool InheritScale);

	private sealed record SlotState(Slot Slot, string Name, Bone Bone, string? SetupAttachment);

	private sealed record AttachmentState(
		Attachment Attachment, string Name, Guid AssetId,
		Vector2 Offset, float Rotation, Vector2 Scale, Vector2 Pivot);

	private sealed record LookState(
		LookConstraint Constraint, Bone Target, LookShape Shape, Vector2 Radius, float Follow, float Mix);
	private sealed record IkState(
	IkConstraint Constraint, string Name, List<Bone> Bones, Bone Target, float Mix, int Bend);

	private readonly Skeleton _skeleton;
	private readonly List<Bone> _roots;
	private readonly List<BoneState> _bones;
	private readonly List<SlotState> _slots;
	private readonly List<(Skin Skin, Dictionary<(string Slot, string Attachment), Attachment> Map)> _skins;
	private readonly Skin? _activeSkin;
	private readonly List<AttachmentState> _attachments;
	private readonly List<KeyValuePair<Guid, Asset>> _assets;
	private readonly List<IkState> _ik;
	private readonly List<LookState> _looks;
	private readonly List<Animation> _animationList;
	private readonly List<Animation> _animationContents;
	private readonly List<AnimationSnapshot> _animations;

	private StructureSnapshot(Skeleton skeleton, List<Animation> animations)
	{
		_skeleton = skeleton;
		_roots = skeleton.Roots.ToList();
		_bones = skeleton.AllBones().Select(b => new BoneState(
			b, b.Parent, b.Children.ToList(), b.Name, b.Length,
			b.SetupPosition, b.SetupRotation, b.SetupScale, b.SetupTurn, b.SetupTilt,
			b.InheritRotation, b.InheritScale)).ToList();

		_slots = skeleton.Slots.Select(s => new SlotState(s, s.Name, s.Bone, s.SetupAttachment)).ToList();
		_skins = skeleton.AllSkins().Select(s => (s, s.CopyMap())).ToList();
		_activeSkin = skeleton.ActiveSkin;
		_attachments = _skins.SelectMany(s => s.Map.Values).Distinct()
			.Select(a => new AttachmentState(a, a.Name, a.AssetId, a.Offset, a.Rotation, a.Scale, a.Pivot))
			.ToList();
		_assets = skeleton.Assets.ToList();

		_ik = skeleton.IkConstraints.Select(c => new IkState(
			c, c.Name, c.Bones.ToList(), c.Target, c.SetupMix, c.BendDirection)).ToList();
		_looks = skeleton.LookConstraints.Select(c => new LookState(
			c, c.Target, c.Shape, c.Radius, c.Follow, c.SetupMix)).ToList();

		_animationList = animations;
		_animationContents = animations.ToList();
		_animations = animations.Select(AnimationSnapshot.Capture).ToList();
	}

	public static StructureSnapshot Capture(Skeleton skeleton, List<Animation> animations) =>
		new(skeleton, animations);

	public void Restore()
	{
		_skeleton.Roots.Clear();
		_skeleton.Roots.AddRange(_roots);
		foreach (var s in _bones)
		{
			s.Bone.RestoreLinks(s.Parent, s.Children);
			s.Bone.Name = s.Name;
			s.Bone.Length = s.Length;
			s.Bone.SetupPosition = s.Position;
			s.Bone.SetupRotation = s.Rotation;
			s.Bone.SetupScale = s.Scale;
			s.Bone.SetupTurn = s.Turn;
			s.Bone.SetupTilt = s.Tilt;
			s.Bone.InheritRotation = s.InheritRotation;
			s.Bone.InheritScale = s.InheritScale;
		}

		_skeleton.Slots.Clear();
		foreach (var s in _slots)
		{
			s.Slot.Name = s.Name;
			s.Slot.Bone = s.Bone;
			s.Slot.SetupAttachment = s.SetupAttachment;
			_skeleton.Slots.Add(s.Slot);
		}

		foreach (var (skin, map) in _skins) skin.RestoreMap(map);
		_skeleton.ActiveSkin = _activeSkin;

		foreach (var a in _attachments)
		{
			a.Attachment.Name = a.Name;
			a.Attachment.AssetId = a.AssetId;
			a.Attachment.Offset = a.Offset;
			a.Attachment.Rotation = a.Rotation;
			a.Attachment.Scale = a.Scale;
			a.Attachment.Pivot = a.Pivot;
		}

		_skeleton.Assets.Clear();
		foreach (var (id, asset) in _assets) _skeleton.Assets[id] = asset;

		_skeleton.IkConstraints.Clear();
		foreach (var s in _ik)
		{
			s.Constraint.Name = s.Name;
			s.Constraint.Bones.Clear();
			s.Constraint.Bones.AddRange(s.Bones);
			s.Constraint.Target = s.Target;
			s.Constraint.SetupMix = s.Mix;
			s.Constraint.BendDirection = s.Bend;
			_skeleton.IkConstraints.Add(s.Constraint);
		}
		_skeleton.LookConstraints.Clear();
		foreach (var s in _looks)
		{
			s.Constraint.Target = s.Target;
			s.Constraint.Shape = s.Shape;
			s.Constraint.Radius = s.Radius;
			s.Constraint.Follow = s.Follow;
			s.Constraint.SetupMix = s.Mix;
			_skeleton.LookConstraints.Add(s.Constraint);
		}

		_animationList.Clear();
		_animationList.AddRange(_animationContents);
		foreach (var a in _animations) a.Restore();
	}
}