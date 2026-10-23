using System.Numerics;

namespace Animator.Core.Editing;

// Scene edits: objects added, renamed, deleted, reordered. Like RigOps, these change the
// model directly; the editor wraps them in undo steps (SceneSnapshot).
public static class SceneOps
{
	// A new rigged object: one root bone to build on, and an empty animation
	public static SceneObject AddRigged(Scene scene, string name, Vector2 position)
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "root", Length = 60, SetupRotation = MathF.PI / 2 });   // pointing up
		var obj = new SceneObject(scene.UniqueName(name), ObjectKind.Rigged, skeleton) { Position = position };
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
		foreach (var other in scene.Objects.Where(o => o.AttachObject == obj.Name))
		{
			other.AttachObject = null;
			other.AttachBone = null;
		}
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
}
