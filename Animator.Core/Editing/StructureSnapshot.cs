using System.Numerics;

namespace Animator.Core.Editing;

// The whole rig: hierarchy, rest poses, parts, skins, constraints, assets and animations.
// Restores the SAME objects instead of copies, so other undo steps keep pointing at the right things.
public sealed class StructureSnapshot
{
	private sealed record BoneState(
		Bone Bone, Bone? Parent, List<Bone> Children, string Name, float Length,
		Vector2 Position, float Rotation, Vector2 Scale, float Turn, float Tilt,
		bool InheritRotation, bool InheritScale, bool Connected);

	private sealed record SlotState(Slot Slot, string Name, Bone Bone, string? SetupAttachment,
		BlendMode Blend, Vector3 Color, float Opacity);

	private sealed record AttachmentState(
		Attachment Attachment, string Name, Guid AssetId,
		Vector2 Offset, float Rotation, Vector2 Scale, Vector2 Pivot);

	private sealed record HitboxState(Hitbox Hitbox, string Name, Bone Bone, HitboxShape Shape, Vector2 Offset, float Rotation,
		Vector2 Size, string Kind, bool Active);
	private sealed record PointState(RigPoint Point, string Name, Bone Bone, Vector2 Offset, float Rotation);

	private sealed record LookState(
		LookConstraint Constraint, Bone Target, LookShape Shape, Vector2 Radius, float Follow, float Mix);
	private sealed record IkState(
	IkConstraint Constraint, string Name, List<Bone> Bones, Bone Target, float Mix, int Bend);

	private readonly Skeleton _skeleton;
	private readonly List<Bone> _roots;
	private readonly List<BoneState> _bones;
	private readonly List<SlotState> _slots;
	private readonly List<(Skin Skin, Dictionary<(string Slot, string Attachment), Attachment> Map, string Name, string Category, bool Overlay, List<ColorLayer> Colours)> _skins;
	private readonly List<Skin> _skinList;
	private readonly List<Skin> _shownSkins;
	private readonly List<ColorLayer> _colorLayers;
	private readonly List<HitboxState> _hitboxes;
	private readonly List<PointState> _points;   // which the object has (a skin deleted takes its own)
	private readonly List<AttachmentState> _attachments;
	private readonly List<KeyValuePair<Guid, Asset>> _assets;
	private readonly List<(Asset Asset, string Name)> _assetNames;   // drawings rename their pixels too
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
			b.InheritRotation, b.InheritScale, b.Connected)).ToList();

		_slots = skeleton.Slots.Select(s => new SlotState(s, s.Name, s.Bone, s.SetupAttachment,
			s.Blend, s.SetupColor, s.SetupOpacity)).ToList();
		_skins = skeleton.AllSkins().Select(s => (s, s.CopyMap(), s.Name, s.Category, s.Overlay, s.ColorLayers.ToList())).ToList();
		_skinList = skeleton.Skins.ToList();
		_shownSkins = skeleton.ShownSkins.ToList();
		_colorLayers = skeleton.ColorLayers.ToList();
		_hitboxes = skeleton.Hitboxes.Select(h => new HitboxState(h, h.Name, h.Bone, h.Shape, h.Offset, h.Rotation, h.Size, h.Kind, h.SetupActive)).ToList();
		_points = skeleton.Points.Select(p => new PointState(p, p.Name, p.Bone, p.Offset, p.Rotation)).ToList();
		_attachments = _skins.SelectMany(s => s.Map.Values).Distinct()
			.Select(a => new AttachmentState(a, a.Name, a.AssetId, a.Offset, a.Rotation, a.Scale, a.Pivot))
			.ToList();
		_assets = skeleton.Assets.ToList();
		_assetNames = skeleton.Assets.Values.Select(a => (a, a.Name)).ToList();

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
			s.Bone.Connected = s.Connected;
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
			s.Slot.Blend = s.Blend;
			s.Slot.SetupColor = s.Color;
			s.Slot.SetupOpacity = s.Opacity;
			_skeleton.Slots.Add(s.Slot);
		}

		foreach (var (skin, map, name, category, overlay, colours) in _skins)
		{
			skin.RestoreMap(map);
			skin.ColorLayers.Clear();
			skin.ColorLayers.AddRange(colours);
			(skin.Name, skin.Category, skin.Overlay) = (name, category, overlay);
		}
		_skeleton.Skins.Clear();
		_skeleton.Skins.AddRange(_skinList);
		_skeleton.ShownSkins.Clear();
		_skeleton.ShownSkins.UnionWith(_shownSkins);
		_skeleton.ColorLayers.Clear();
		_skeleton.ColorLayers.AddRange(_colorLayers);
		_skeleton.Hitboxes.Clear();
		foreach (var h in _hitboxes)
		{
			(h.Hitbox.Name, h.Hitbox.Bone, h.Hitbox.Shape, h.Hitbox.Offset, h.Hitbox.Rotation) = (h.Name, h.Bone, h.Shape, h.Offset, h.Rotation);
			(h.Hitbox.Size, h.Hitbox.Kind, h.Hitbox.SetupActive, h.Hitbox.Active) = (h.Size, h.Kind, h.Active, h.Active);
			_skeleton.Hitboxes.Add(h.Hitbox);
		}
		_skeleton.Points.Clear();
		foreach (var p in _points)
		{
			(p.Point.Name, p.Point.Bone, p.Point.Offset, p.Point.Rotation) = (p.Name, p.Bone, p.Offset, p.Rotation);
			_skeleton.Points.Add(p.Point);
		}

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
		foreach (var (asset, name) in _assetNames) asset.Name = name;

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