using System.Numerics;

namespace Animator.Core.Editing;

// Part (slot) edits. Like RigOps: changes the model directly; the editor adds undo.
public static class PartOps
{
	// Rows that animate a part (as opposed to a bone; parts and bones can share names)
	public static bool TargetsPart(Timeline t) =>
		t is AttachmentTimeline or PartTranslateTimeline or PartRotateTimeline or PartScaleTimeline
			or PartColorTimeline or PartOpacityTimeline or DeformTimeline;

	public static string UniqueSlotName(Skeleton skeleton, string baseName)
	{
		var used = skeleton.Slots.Select(s => s.Name).ToHashSet();
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
		{
			string candidate = $"{baseName}{i}";
			if (!used.Contains(candidate)) return candidate;
		}
	}

	// Adds an (already filled) asset as a new part on a bone, drawn on top of everything
	public static Slot CreatePart(Skeleton skeleton, Bone bone, Asset asset, string name,
								  Vector2 offset, float rotation)
	{
		name = UniqueSlotName(skeleton, string.IsNullOrWhiteSpace(name) ? "part" : name.Trim());
		asset.Name = name;
		skeleton.Assets[asset.Id] = asset;

		var slot = new Slot(name, bone) { SetupAttachment = name, ActiveAttachment = name };   // shown right away
		skeleton.Slots.Add(slot);
		skeleton.DefaultSkin.Set(name, new Attachment
		{
			Name = name,
			AssetId = asset.Id,
			Offset = offset,
			Rotation = rotation
		});
		return slot;
	}

	public static void DeletePart(Skeleton skeleton, IEnumerable<Animation> animations, Slot slot)
	{
		MaskOps.Forget(skeleton, slot);
		skeleton.Slots.Remove(slot);
		foreach (var skin in skeleton.AllSkins()) skin.RemoveSlot(slot.Name);
		foreach (var anim in animations)
			anim.Timelines.RemoveAll(t => TargetsPart(t) && t.Target == slot.Name);
		DrawOrderOps.Forget(animations, new HashSet<string> { slot.Name });
		RemoveUnusedAssets(skeleton);
	}

	// Drops assets no attachment uses any more, so they aren't saved.
	// Their pixels stay in the AssetStore, so undo can still bring them back.
	public static void RemoveUnusedAssets(Skeleton skeleton)
	{
		var used = skeleton.AllAttachments().Select(a => a.AssetId).ToHashSet();
		foreach (var id in skeleton.Assets.Keys.Where(id => !used.Contains(id)).ToList())
			skeleton.Assets.Remove(id);
	}

	public static bool RenamePart(Skeleton skeleton, IEnumerable<Animation> animations, Slot slot, string newName)
	{
		newName = newName.Trim();
		if (newName.Length == 0 || newName == slot.Name) return false;
		if (skeleton.Slots.Any(s => s != slot && s.Name == newName)) return false;

		string oldName = slot.Name;
		slot.Name = newName;
		foreach (var skin in skeleton.AllSkins()) skin.RenameSlot(oldName, newName);
		foreach (var anim in animations)
			foreach (var t in anim.Timelines)
				if (TargetsPart(t) && t.Target == oldName) t.Target = newName;
		DrawOrderOps.Rename(animations, oldName, newName);
		return true;
	}

	// +1 = toward the front (drawn later), -1 = toward the back
	public static bool MoveInDrawOrder(Skeleton skeleton, Slot slot, int direction)
	{
		int i = skeleton.Slots.IndexOf(slot);
		int j = i + direction;
		if (i < 0 || j < 0 || j >= skeleton.Slots.Count) return false;
		skeleton.Slots.RemoveAt(i);
		skeleton.Slots.Insert(j, slot);
		return true;
	}

	// Puts the part on another bone, keeping it where it is in the rest pose
	public static bool MoveToBone(Skeleton skeleton, Slot slot, Bone newBone)
	{
		if (slot.Bone == newBone) return false;

		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		if (!Matrix3x2.Invert(newBone.World, out var toNewBone)) return false;

		var attachments = skeleton.AllSkins().SelectMany(s => s.AttachmentsFor(slot.Name)).Distinct();
		foreach (var att in attachments)
			PlaceFrame(att, FrameOf(att) * slot.Bone.World, newBone.World);   // the same place, measured from the new bone

		slot.Bone = newBone;
		return true;
	}

	// A drawing's placement on its bone: scaled, turned, then moved (the pivot sits at the offset)
	public static Matrix3x2 FrameOf(Attachment drawing) =>
		Matrix3x2.CreateScale(drawing.Scale)
		* Matrix3x2.CreateRotation(drawing.Rotation)
		* Matrix3x2.CreateTranslation(drawing.Offset);

	// Sets a drawing's placement so it lands at `frameWorld` (a FrameOf times a bone's world)
	// on a bone whose world transform is `boneWorld`
	public static void PlaceFrame(Attachment drawing, Matrix3x2 frameWorld, Matrix3x2 boneWorld)
	{
		if (!Matrix3x2.Invert(boneWorld, out var toBone)) return;   // a bone squashed flat
		var local = frameWorld * toBone;

		float sx = new Vector2(local.M11, local.M12).Length();
		float sy = new Vector2(local.M21, local.M22).Length();
		if (local.M11 * local.M22 - local.M12 * local.M21 < 0) sy = -sy;   // mirrored

		drawing.Offset = local.Translation;
		drawing.Rotation = MathF.Atan2(local.M12, local.M11);
		drawing.Scale = new Vector2(sx, sy);
	}

	// Moves the pivot to an image pixel WITHOUT moving the image: the offset compensates
	public static void SetPivotKeepingPlace(Attachment att, int width, int height, Vector2 pivotPixel)
	{
		var local = att.LocalMatrix(width, height);
		att.Offset = Vector2.Transform(pivotPixel, local);   // where that pixel is right now
		att.Pivot = new Vector2(pivotPixel.X / width, pivotPixel.Y / height);
	}
}