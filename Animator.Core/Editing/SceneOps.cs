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

	// ======================= clips on the scene timeline =======================
	// A track never has two clips at the same time: clips can't overlap or pass each other.
	// minLength is the shortest a clip may be (one frame, in seconds).

	// A clip of `animation` on the object's track at `start`, one full play long. If `start` is
	// inside another clip it goes right after that one, and it's shortened to fit before the next.
	// null = there's no room there.
	public static SceneClip? AddClip(SceneObject obj, Animation animation, float start, float minLength)
	{
		SortClips(obj);
		start = MathF.Max(0f, start);
		foreach (var c in obj.Clips)   // in time order, so a run of clips is skipped as a whole
			if (start >= c.Start && start < c.End) start = c.End;

		float room = obj.Clips.Where(c => c.Start >= start).Select(c => c.Start).DefaultIfEmpty(float.PositiveInfinity).Min() - start;
		float length = MathF.Min(MathF.Max(animation.Duration, minLength), room);
		if (length < minLength - 1e-5f) return null;

		var clip = new SceneClip(animation, start, length);
		obj.Clips.Add(clip);
		SortClips(obj);
		return clip;
	}

	// Slides a clip along its track, between its neighbours and not before the scene's start
	public static void MoveClip(SceneObject obj, SceneClip clip, float start)
	{
		var (prev, next) = Neighbours(obj, clip);
		float min = prev?.End ?? 0f;
		float max = (next?.Start ?? float.PositiveInfinity) - clip.Length;
		clip.Start = Math.Clamp(start, min, MathF.Max(min, max));
	}

	// Drags the clip's end: longer loops (or holds) the animation, shorter cuts it
	public static void SetClipEnd(SceneObject obj, SceneClip clip, float end, float minLength)
	{
		var (_, next) = Neighbours(obj, clip);
		float min = clip.Start + minLength;
		float max = next?.Start ?? float.PositiveInfinity;
		clip.Length = Math.Clamp(end, min, MathF.Max(min, max)) - clip.Start;
	}

	// Drags the clip's start, keeping its end: trims frames off the front of the animation (or
	// brings them back), so what's left stays where it was in time. Never before the previous
	// clip, the scene's start, or the animation's first frame.
	public static void SetClipStart(SceneObject obj, SceneClip clip, float start, float minLength)
	{
		var (prev, _) = Neighbours(obj, clip);
		float end = clip.End;
		float min = MathF.Max(prev?.End ?? 0f, clip.Start - clip.Offset);
		float max = end - minLength;
		start = Math.Clamp(start, min, MathF.Max(min, max));
		clip.Offset = MathF.Max(0f, clip.Offset + start - clip.Start);
		clip.Start = start;
		clip.Length = end - start;
	}

	// Exactly one play of the animation (from the clip's offset), as far as there's room.
	// false = it already was.
	public static bool FitClip(SceneObject obj, SceneClip clip, float minLength)
	{
		float before = clip.Length;
		SetClipEnd(obj, clip, clip.Start + MathF.Max(minLength, clip.Animation.Duration - clip.Offset), minLength);
		return MathF.Abs(clip.Length - before) > 1e-6f;
	}

	// The end of the last clip in the scene (0 without clips): where an animation scene finishes
	public static float LastClipEnd(Scene scene) =>
		scene.Objects.SelectMany(o => o.LiveClips).Select(c => c.End).DefaultIfEmpty(0f).Max();

	private static (SceneClip? Prev, SceneClip? Next) Neighbours(SceneObject obj, SceneClip clip)
	{
		SortClips(obj);
		int i = obj.Clips.IndexOf(clip);
		return (i > 0 ? obj.Clips[i - 1] : null, i >= 0 && i < obj.Clips.Count - 1 ? obj.Clips[i + 1] : null);
	}

	private static void SortClips(SceneObject obj) => obj.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));

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
