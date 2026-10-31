using System.Numerics;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// Hitboxes and points (the rig panel's Boxes view): added to bones, fitted to parts, keyed on and
// off, renamed and deleted with their keys. Like the other ops, these change the model directly;
// the editor adds undo.
public static class HitboxOps
{
	public static string UniqueHitboxName(Skeleton skeleton, string baseName) =>
		Unique(baseName, n => skeleton.FindHitbox(n) == null);

	public static string UniquePointName(Skeleton skeleton, string baseName) =>
		Unique(baseName, n => skeleton.FindPoint(n) == null);

	private static string Unique(string baseName, Func<string, bool> free)
	{
		baseName = baseName.Trim().Length > 0 ? baseName.Trim() : "box";
		if (free(baseName)) return baseName;
		for (int i = 2; ; i++)
			if (free($"{baseName} {i}")) return $"{baseName} {i}";
	}

	public static Hitbox AddHitbox(Skeleton skeleton, Bone bone, HitboxShape shape, string kind = "hurt", string? name = null)
	{
		var hitbox = new Hitbox(UniqueHitboxName(skeleton, name ?? $"{bone.Name} {kind}"), bone)
		{
			Shape = shape,
			Kind = kind,
			Offset = new Vector2(bone.Length / 2f, 0f),   // the middle of the bone
			Size = shape == HitboxShape.Box ? new Vector2(MathF.Max(20f, bone.Length), 30f) : new Vector2(MathF.Max(20f, bone.Length * 0.8f))
		};
		skeleton.Hitboxes.Add(hitbox);
		return hitbox;
	}

	public static RigPoint AddPoint(Skeleton skeleton, Bone bone, string? name = null)
	{
		var point = new RigPoint(UniquePointName(skeleton, name ?? $"{bone.Name} point"), bone) { Offset = new Vector2(bone.Length, 0f) };   // at its tip
		skeleton.Points.Add(point);
		return point;
	}

	public static void DeleteHitbox(Skeleton skeleton, IEnumerable<Animation> animations, Hitbox hitbox)
	{
		skeleton.Hitboxes.Remove(hitbox);
		foreach (var animation in animations)
			animation.Timelines.RemoveAll(t => t is HitboxTimeline && t.Target == hitbox.Name);
	}

	public static bool RenameHitbox(Skeleton skeleton, IEnumerable<Animation> animations, Hitbox hitbox, string name)
	{
		name = name.Trim();
		if (name.Length == 0 || name == hitbox.Name || skeleton.FindHitbox(name) != null) return false;
		foreach (var animation in animations)
			foreach (var t in animation.Timelines)
				if (t is HitboxTimeline && t.Target == hitbox.Name) t.Target = name;
		hitbox.Name = name;
		return true;
	}

	public static bool RenamePoint(Skeleton skeleton, RigPoint point, string name)
	{
		name = name.Trim();
		if (name.Length == 0 || name == point.Name || skeleton.FindPoint(name) != null) return false;
		point.Name = name;
		return true;
	}

	// On (or off) from this frame on, in this animation; false if it already is
	public static bool KeyActive(Animation animation, Hitbox hitbox, int frame, int fps, bool on)
	{
		float time = frame / (float)fps;
		var row = animation.Find<HitboxTimeline>(hitbox.Name);
		bool now = row != null && row.Track.Keys.Count > 0 && time >= row.Track.Keys[0].Time - KeyHelpers.Epsilon && row.Track.TrySample(time, out var keyed)
			? keyed : hitbox.SetupActive;
		int here = row?.Track.Keys.FindIndex(k => MathF.Abs(k.Time - time) < KeyHelpers.Epsilon) ?? -1;
		if (now == on && here < 0) return false;
		if (here >= 0 && row!.Track.Keys[here].Value == on) return false;
		animation.GetOrCreate(hitbox.Name, n => new HitboxTimeline(n)).Add(time, on);
		return true;
	}

	// Fitted around a part as it rests (the skeleton in its rest pose): over its picture, or just
	// these pixels of it (x, y, width, height: its visible ones, say). A box turns with the
	// picture; a circle goes round it. False if the part shows nothing.
	public static bool FitToPart(Skeleton skeleton, Hitbox hitbox, Slot slot, (float X, float Y, float W, float H)? pixels = null)
	{
		if (slot.SetupAttachment is not { } name || skeleton.FindAttachment(slot.Name, name) is not { } drawing
			|| !skeleton.Assets.TryGetValue(drawing.AssetId, out var asset) || asset.Width <= 0 || asset.Height <= 0) return false;
		if (!Matrix3x2.Invert(hitbox.Bone.World, out var toBone)) return false;
		var (x, y, w, h) = pixels ?? (0f, 0f, asset.Width, asset.Height);
		var toLocal = drawing.LocalMatrix(asset.Width, asset.Height) * slot.Bone.World * toBone;   // picture → the hitbox's bone
		var corners = new[] { new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h) }
			.Select(p => Vector2.Transform(p, toLocal)).ToArray();

		// Measured along the picture's own sides
		var along = Vector2.TransformNormal(Vector2.UnitX, toLocal);
		float angle = MathF.Atan2(along.Y, along.X);
		var unturn = Matrix3x2.CreateRotation(-angle);
		var flat = corners.Select(p => Vector2.Transform(p, unturn)).ToArray();
		float minX = flat.Min(p => p.X), maxX = flat.Max(p => p.X), minY = flat.Min(p => p.Y), maxY = flat.Max(p => p.Y);
		var centre = Vector2.Transform(new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f), Matrix3x2.CreateRotation(angle));

		hitbox.Offset = centre;
		if (hitbox.Shape == HitboxShape.Box)
		{
			hitbox.Rotation = MathUtil.ShortestAngle(angle);
			hitbox.Size = new Vector2(maxX - minX, maxY - minY);
		}
		else
		{
			hitbox.Rotation = 0f;
			hitbox.Size = new Vector2((maxX - minX + maxY - minY) / 2f);   // round the middle of its sides
		}
		return true;
	}

	// ----- bones going away -----

	// Bones deleted: their hitboxes and points go too, with the hitboxes' keys
	public static void ForgetBones(Skeleton skeleton, IEnumerable<Animation> animations, IReadOnlySet<Bone> doomed)
	{
		var names = skeleton.Hitboxes.Where(h => doomed.Contains(h.Bone)).Select(h => h.Name).ToHashSet();
		skeleton.Hitboxes.RemoveAll(h => doomed.Contains(h.Bone));
		skeleton.Points.RemoveAll(p => doomed.Contains(p.Bone));
		foreach (var animation in animations)
			animation.Timelines.RemoveAll(t => t is HitboxTimeline && names.Contains(t.Target));
	}

	// A bone's hitboxes and points to another bone, staying where they are (the rest pose)
	public static void MoveToBone(Skeleton skeleton, Bone from, Bone to)
	{
		foreach (var hitbox in skeleton.Hitboxes.Where(h => h.Bone == from)) SetBone(hitbox, to);
		foreach (var point in skeleton.Points.Where(p => p.Bone == from)) SetBone(point, to);
	}

	// On another bone, staying where it is (as the bones are posed now)
	public static bool SetBone(Hitbox hitbox, Bone to)
	{
		if (to == hitbox.Bone || !Matrix3x2.Invert(to.World, out var toNew)) return false;
		(hitbox.Offset, hitbox.Rotation) = Rehang(hitbox.World * toNew);
		hitbox.Bone = to;
		return true;
	}

	public static bool SetBone(RigPoint point, Bone to)
	{
		if (to == point.Bone || !Matrix3x2.Invert(to.World, out var toNew)) return false;
		(point.Offset, point.Rotation) = Rehang(point.World * toNew);
		point.Bone = to;
		return true;
	}

	private static (Vector2 Offset, float Rotation) Rehang(Matrix3x2 local) =>
		(local.Translation, MathF.Atan2(local.M12, local.M11));
}
