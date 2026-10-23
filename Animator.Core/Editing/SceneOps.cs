using System.Numerics;

namespace Animator.Core.Editing;

// Scene edits: objects added, renamed, deleted, reordered, attached. Like RigOps, these change
// the model directly; the editor wraps them in undo steps (SceneSnapshot).
public static class SceneOps
{
	// A new rigged object: one root bone to build on, and an empty animation
	public static SceneObject AddRigged(Scene scene, string name, Vector2 position)
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "root", Length = 60, SetupRotation = MathF.PI / 2 });   // pointing up
		return Add(scene, new SceneObject(scene.UniqueName(name), ObjectKind.Rigged, skeleton), position);
	}

	// A new drawn object: no visible rig, just parts (layers) to draw on. Inside it there's one
	// hidden root bone, with no length, so its parts sit around the object's origin and
	// drawings, placement keys and effects work exactly like on a rigged object.
	public static SceneObject AddDrawn(Scene scene, string name, Vector2 position)
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "root", Length = 0 });
		return Add(scene, new SceneObject(scene.UniqueName(name), ObjectKind.Drawn, skeleton), position);
	}

	private static SceneObject Add(Scene scene, SceneObject obj, Vector2 position)
	{
		obj.Position = position;
		obj.Animations.Add(new Animation { Name = "animation", Duration = 2f, Loop = true });
		scene.Objects.Add(obj);
		return obj;
	}

	// Objects attached to this one follow the new name
	public static bool RenameObject(Scene scene, SceneObject obj, string newName)
	{
		newName = newName.Trim();
		if (newName.Length == 0 || newName == obj.Name || scene.Find(newName) != null) return false;
		foreach (var other in scene.Objects.Where(o => o.AttachObject == obj.Name))
			other.AttachObject = newName;
		obj.Name = newName;
		return true;
	}

	// A scene keeps at least one object. Objects attached to it are let go, where they are.
	public static bool DeleteObject(Scene scene, SceneObject obj)
	{
		if (scene.Objects.Count <= 1 || !scene.Objects.Contains(obj)) return false;
		foreach (var other in scene.Objects.Where(o => o.AttachObject == obj.Name).ToList())
			Attach(scene, other, null, null);
		scene.Objects.Remove(obj);
		return true;
	}

	// A copy of an object, just in front of it, with its own rig, animations and drawings.
	// The editor copies the pixels: LayerSources says which old layer each new layer copies.
	public static (SceneObject Copy, Dictionary<Guid, Guid> LayerSources) Duplicate(Scene scene, SceneObject source)
	{
		var (copy, layers) = ProjectMapper.CloneObject(source, NameForCopy(scene, source.Name));
		scene.Objects.Insert(scene.Objects.IndexOf(source) + 1, copy);
		return (copy, layers);
	}

	// "tree" → "tree2", and "tree2" → "tree3" (not "tree22")
	private static string NameForCopy(Scene scene, string name)
	{
		string stem = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
		if (stem.Length == 0) stem = name;
		for (int i = 2; ; i++)
		{
			string candidate = $"{stem}{i}";
			if (scene.Find(candidate) == null) return candidate;
		}
	}

	// +1 = toward the front (drawn later), -1 = toward the back
	public static bool MoveInOrder(Scene scene, SceneObject obj, int direction)
	{
		int i = scene.Objects.IndexOf(obj);
		int j = i + direction;
		if (i < 0 || j < 0 || j >= scene.Objects.Count) return false;
		scene.Objects.RemoveAt(i);
		scene.Objects.Insert(j, obj);
		return true;
	}

	// ======================= attaching =======================

	// Puts an object on another object's bone (holder null = free in the scene), keeping it
	// exactly where it is on screen right now: its own place is re-measured from the new parent.
	// false = nothing changed, the bone doesn't exist, or the holder is carried by this object
	// (it would end up carrying itself).
	public static bool Attach(Scene scene, SceneObject obj, SceneObject? holder, string? boneName)
	{
		if (holder == null || boneName == null)
		{
			holder = null;
			boneName = null;
		}
		if (obj.AttachObject == holder?.Name && obj.AttachBone == boneName) return false;
		if (holder != null && (holder == obj || Carries(scene, obj, holder) || holder.Skeleton.FindBone(boneName!) == null))
			return false;

		var world = scene.WorldTransform(obj);
		obj.AttachObject = holder?.Name;
		obj.AttachBone = boneName;
		if (Matrix3x2.Invert(scene.ParentTransform(obj), out var toParent))
			SetTransform(obj, world * toParent);
		return true;
	}

	// Does `obj` carry `other`, directly or through objects attached to each other?
	public static bool Carries(Scene scene, SceneObject obj, SceneObject other)
	{
		var seen = new HashSet<SceneObject>();
		for (var o = other; o.AttachObject is { } name && seen.Add(o);)
		{
			if (scene.Find(name) is not { } holder) return false;
			if (holder == obj) return true;
			o = holder;
		}
		return false;
	}

	// A holder's bone was renamed: objects attached to it follow
	public static void RenameHolderBone(Scene scene, SceneObject holder, string oldName, string newName)
	{
		foreach (var o in scene.Objects.Where(o => o.AttachObject == holder.Name && o.AttachBone == oldName))
			o.AttachBone = newName;
	}

	// Bones of the holder are about to be deleted: what they carry is let go, where it is
	public static void ReleaseFromBones(Scene scene, SceneObject holder, IReadOnlySet<string> boneNames)
	{
		foreach (var o in scene.Objects.Where(o => o.AttachObject == holder.Name && o.AttachBone is { } b && boneNames.Contains(b)).ToList())
			Attach(scene, o, null, null);
	}

	// Position, rotation and scale that make exactly this matrix (scale, then turn, then move).
	// A matrix with shear (a parent scaled unevenly and turned) is matched as closely as these allow.
	public static void SetTransform(SceneObject obj, Matrix3x2 m)
	{
		float sx = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
		float det = m.M11 * m.M22 - m.M12 * m.M21;
		obj.Position = m.Translation;
		obj.Rotation = MathF.Atan2(m.M12, m.M11);
		obj.Scale = new Vector2(sx, sx > 1e-6f ? det / sx : MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22));
	}
}
