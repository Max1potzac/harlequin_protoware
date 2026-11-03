using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer;

// The scene, edited (EditorSession's side of the Objects panel, the object properties and the
// scene timeline): objects added, copied, deleted, reordered, placed, set up and attached; the
// scene's length, loop and stage; clips added, moved, changed and deleted. Each edit is one undo
// step, and says whether it changed anything (false: nothing to undo, nothing was changed).
public sealed partial class EditorSession
{
	// ======================= undo steps =======================

	// One scene edit as one undo step. The change returns the step's name, or null if nothing
	// changed (anything it did is put back).
	public bool EditScene(Func<string?> change)
	{
		var before = SceneSnapshot.Capture(Scene);
		string? name = change();
		if (name == null)
		{
			before.Restore();
			return false;
		}
		State.Undo.Push(new SnapshotCommand<SceneSnapshot>(name, before, SceneSnapshot.Capture(Scene), s => s.Restore()));
		return true;
	}

	// A rig edit that can also change the scene (objects carried by a bone that's renamed or
	// deleted), as one undo step
	public bool EditStructureAndScene(Func<string?> change)
	{
		var sceneBefore = SceneSnapshot.Capture(Scene);
		var rigBefore = State.CaptureStructure();
		string? name = change();
		if (name == null)
		{
			rigBefore.Restore();
			sceneBefore.Restore();
			return false;
		}
		State.Undo.Push(new CompositeCommand(name,
			new SnapshotCommand<StructureSnapshot>(name, rigBefore, State.CaptureStructure(), s => s.Restore()),
			new SnapshotCommand<SceneSnapshot>(name, sceneBefore, SceneSnapshot.Capture(Scene), s => s.Restore())));
		return true;
	}

	// ======================= objects =======================

	// A new rigged object at a place in the scene (a rig to be built: open it next)
	public SceneObject AddRiggedObject(Vector2 at, string name = "object")
	{
		SceneObject? created = null;
		EditScene(() =>
		{
			created = SceneOps.AddRigged(Scene, name, at);
			return $"New object {created.Name}";
		});
		return created!;
	}

	// A new drawn object at a place in the scene, with one part of a picture this size: these
	// pixels, or a blank layer (null) to paint on
	public (SceneObject Object, Slot Part, Asset Drawing) AddDrawnObject(string name, SKBitmap? pixels, int width, int height, Vector2 at)
	{
		SceneObject? created = null;
		Slot? part = null;
		Asset? drawing = null;
		EditScene(() =>
		{
			created = SceneOps.AddDrawn(Scene, name, at);
			drawing = new Asset { Name = name, Width = width, Height = height };
			Assets.AddLayer(drawing, "Base", pixels);
			part = PartOps.CreatePart(created.Skeleton, created.Skeleton.Roots[0], drawing, name, Vector2.Zero, 0f);
			return $"New drawn object {created.Name}";
		});
		return (created!, part!, drawing!);
	}

	// A copy of an object (its pictures copied too), a little aside so it shows
	public SceneObject CopyObject(SceneObject source)
	{
		SceneObject? copy = null;
		EditScene(() =>
		{
			var (made, layers) = SceneOps.Duplicate(Scene, source);
			foreach (var (newLayer, oldLayer) in layers) Assets.CopyPixels(oldLayer, newLayer);
			made.Position += new Vector2(30, -30);
			copy = made;
			return $"Copy object {source.Name}";
		});
		return copy!;
	}

	// An object gone from the scene (not the last one). The open one gone: the first one opens.
	public bool DeleteObject(SceneObject obj)
	{
		if (!EditScene(() => SceneOps.DeleteObject(Scene, obj) ? $"Delete object {obj.Name}" : null)) return false;
		if (SceneState.Selected == obj) SceneState.Selected = null;
		if (obj == Active) Open(Scene.Objects[0]);
		return true;
	}

	// An object a step toward the front (+1) or the back (-1) of the scene
	public bool MoveObjectInOrder(SceneObject obj, int direction) =>
		EditScene(() => SceneOps.MoveInOrder(Scene, obj, direction) ? $"{obj.Name} {(direction > 0 ? "forward" : "backward")}" : null);

	// False: the name is empty or another object's
	public bool RenameObject(SceneObject obj, string newName)
	{
		newName = newName.Trim();
		return newName != obj.Name && EditScene(() => SceneOps.RenameObject(Scene, obj, newName) ? $"Rename object to {newName}" : null);
	}

	// Where an object is in the scene: the values given change (rotation in radians)
	public bool PlaceObject(SceneObject obj, Vector2? position = null, float? rotation = null, Vector2? scale = null) =>
		EditScene(() =>
		{
			var before = (obj.Position, obj.Rotation, obj.Scale);
			if (position is { } p) obj.Position = p;
			if (rotation is { } r) obj.Rotation = r;
			if (scale is { } s) obj.Scale = s;
			return (obj.Position, MathF.Round(obj.Rotation, 6), obj.Scale) == (before.Position, MathF.Round(before.Rotation, 6), before.Scale)
				? null : $"Place object {obj.Name}";
		});

	// Shown, locked against picking in the viewport, exported, shown in onion skins
	public bool SetObjectFlags(SceneObject obj, bool visible, bool locked, bool export, bool onion) =>
		EditScene(() =>
		{
			if (obj.Visible == visible && obj.Locked == locked && obj.Export == export && obj.Onion == onion) return null;
			(obj.Visible, obj.Locked, obj.Export, obj.Onion) = (visible, locked, export, onion);
			return $"Settings of object {obj.Name}";
		});

	// Free in the scene, then every bone an object could hang on: not its own, and not those of
	// objects it carries (they'd end up carrying each other). A drawn object counts as one bone.
	public List<AttachChoice> AttachChoices(SceneObject obj)
	{
		var list = new List<AttachChoice> { new(null, null, "(nothing: free in the scene)") };
		foreach (var other in Scene.Objects)
		{
			if (other == obj || SceneOps.Carries(Scene, obj, other)) continue;
			if (other.Kind == ObjectKind.Drawn)
			{
				if (other.Skeleton.Roots.FirstOrDefault() is { } root) list.Add(new(other, root.Name, other.Name));
				continue;
			}
			foreach (var bone in other.Skeleton.AllBones())
				list.Add(new(other, bone.Name, $"{other.Name} ▸ {bone.Name}"));
		}
		return list;
	}

	public bool AttachObject(SceneObject obj, AttachChoice choice) =>
		EditScene(() => SceneOps.Attach(Scene, obj, choice.Holder, choice.Bone)
			? (choice.Holder == null ? $"Free {obj.Name}" : $"Attach {obj.Name} to {choice.Label}")
			: null);

	// The animation an object plays in the scene where no clip of it does
	public bool SetPreview(SceneObject obj, string animation) =>
		EditScene(() =>
		{
			if (ScenePlayer.Preview(obj)?.Name == animation || obj.Animations.All(a => a.Name != animation)) return null;
			obj.PreviewAnimation = animation;
			return $"{obj.Name} previews {animation}";
		});

	// ======================= the scene =======================

	public int ToFrames(float seconds) => (int)MathF.Round(seconds * State.Fps);
	public float ToSeconds(int frames) => frames / (float)Math.Max(1, State.Fps);

	// The scene's length in frames (1 or more); the playhead stays inside
	public bool SetSceneLength(int frames)
	{
		frames = Math.Max(1, frames);
		bool changed = EditScene(() =>
		{
			if (frames == ToFrames(Scene.Duration)) return null;
			Scene.Duration = ToSeconds(frames);
			return $"Scene length: {frames} frames";
		});
		SceneState.Time = Math.Min(SceneState.Time, Scene.Duration);
		return changed;
	}

	// The scene as long as its clips run (false: there are none, or it is already)
	public bool FitSceneToClips()
	{
		float end = SceneOps.LastClipEnd(Scene);
		return end > 0 && SetSceneLength(ToFrames(end));
	}

	public bool SetSceneLoop(bool loop) =>
		EditScene(() =>
		{
			if (Scene.Loop == loop) return null;
			Scene.Loop = loop;
			return loop ? "Scene loops" : "Scene plays once";
		});

	// The stage, in pixels (0 = none)
	public bool SetStage(int width, int height) =>
		EditScene(() =>
		{
			width = Math.Max(0, width);
			height = Math.Max(0, height);
			if (Scene.StageWidth == width && Scene.StageHeight == height) return null;
			(Scene.StageWidth, Scene.StageHeight) = (width, height);
			return $"Stage: {width} × {height}";
		});

	// ======================= clips =======================

	// A clip of an animation on the object's track from a scene frame, one full play long
	// (null: no room there, the next clip starts right away). It's selected.
	public SceneClip? AddClip(SceneObject obj, Animation animation, int frame)
	{
		SceneClip? added = null;
		if (!EditScene(() =>
		{
			added = SceneOps.AddClip(obj, animation, ToSeconds(frame), FrameTime);
			return added != null ? $"Add clip {animation.Name} to {obj.Name}" : null;
		})) return null;
		SceneState.Selected = obj;
		SceneState.SelectedClip = added;
		return added;
	}

	// Where a clip starts and how long it is, in frames (as far as the clips next to it allow),
	// where in its animation it starts, and how long it fades in from the clip before
	public bool SetClipTiming(SceneObject obj, SceneClip clip, int? start = null, int? length = null, int? offset = null, int? mixIn = null) =>
		EditScene(() =>
		{
			var before = (clip.Start, clip.Length, clip.Offset, clip.MixIn);
			if (start is { } s) SceneOps.MoveClip(obj, clip, ToSeconds(Math.Max(0, s)));
			if (length is { } l) SceneOps.SetClipEnd(obj, clip, clip.Start + ToSeconds(Math.Max(1, l)), FrameTime);
			if (offset is { } o) clip.Offset = Math.Clamp(ToSeconds(o), 0f, MathF.Max(0f, clip.Animation.Duration - FrameTime));
			if (mixIn is { } m) clip.MixIn = Math.Clamp(ToSeconds(m), 0f, clip.Length);
			return (clip.Start, clip.Length, clip.Offset, clip.MixIn) == before ? null : $"Clip {clip.Animation.Name} of {obj.Name}";
		});

	// The fade in as the object's mix plan has it, from the clip before's animation to this
	// one's (false: no clip before, or it is already)
	public bool MixClipFromPlan(SceneObject obj, SceneClip clip)
	{
		if (obj.ClipBefore(clip) is not { } before) return false;
		float mix = obj.Skeleton.Mixes.Duration(before.Animation.Name, clip.Animation.Name);
		return EditScene(() =>
		{
			float was = clip.MixIn;
			clip.MixIn = Math.Clamp(mix, 0f, clip.Length);
			return clip.MixIn == was ? null : $"Clip {clip.Animation.Name} of {obj.Name}: fade in as mixed";
		});
	}

	public bool SetClipAnimation(SceneClip clip, Animation animation) =>
		EditScene(() =>
		{
			if (clip.Animation == animation) return null;
			clip.Animation = animation;
			clip.Offset = Math.Min(clip.Offset, MathF.Max(0f, animation.Duration - FrameTime));
			return $"Clip plays {animation.Name}";
		});

	// Looping, or holding its last frame
	public bool SetClipLoop(SceneClip clip, bool loop) =>
		EditScene(() =>
		{
			if (clip.Loop == loop) return null;
			clip.Loop = loop;
			return loop ? $"Clip {clip.Animation.Name} loops" : $"Clip {clip.Animation.Name} holds its last frame";
		});

	// A clip one play of its animation long
	public bool FitClip(SceneObject obj, SceneClip clip) =>
		EditScene(() => SceneOps.FitClip(obj, clip, FrameTime) ? $"Clip {clip.Animation.Name}: one play long" : null);

	public bool DeleteClip(SceneObject obj, SceneClip clip)
	{
		bool deleted = EditScene(() => obj.Clips.Remove(clip) ? $"Delete clip {clip.Animation.Name} of {obj.Name}" : null);
		if (SceneState.SelectedClip == clip) SceneState.SelectedClip = null;
		return deleted;
	}
}

// Where an object can hang: free in the scene (no holder), or on a bone of another object
public sealed record AttachChoice(SceneObject? Holder, string? Bone, string Label)
{
	public override string ToString() => Label;
}
