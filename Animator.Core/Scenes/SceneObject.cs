using System.Numerics;

namespace Animator.Core.Scenes;

// Rigged = a character or prop with bones. Drawn = no visible rig, just parts (layers) with
// drawings: backgrounds, effects, frame-by-frame props. A drawn object still has one hidden
// root bone inside, so drawings, onion skin, placement keys and effects all work the same.
public enum ObjectKind { Rigged, Drawn }

// One object in a scene: its own rig and animations, plus where it sits and how it's shown
public class SceneObject
{
	public string Name { get; set; }
	public ObjectKind Kind { get; set; }
	public Skeleton Skeleton { get; set; }
	public List<Animation> Animations { get; set; } = new();

	// Place in the scene (the object's whole rig is drawn through this)
	public Vector2 Position { get; set; }
	public float Rotation { get; set; }                  // radians
	public Vector2 Scale { get; set; } = Vector2.One;

	public bool Visible { get; set; } = true;
	public bool Locked { get; set; }                     // can't be picked or edited in the scene
	public bool Export { get; set; } = true;
	public bool Onion { get; set; } = true;              // shows onion-skin ghosts

	// Its clips on the scene timeline, in time order. With none, the object loops its preview
	// animation (the one to show while you look at the scene, or work on other objects).
	public List<SceneClip> Clips { get; } = new();
	public string? PreviewAnimation { get; set; }

	// Optionally carried by another object's bone (a sword in a hand). Null = free in the scene.
	public string? AttachObject { get; set; }
	public string? AttachBone { get; set; }

	public SceneObject(string name, ObjectKind kind, Skeleton skeleton)
	{
		Name = name;
		Kind = kind;
		Skeleton = skeleton;
	}

	// Object space → scene: scale, then turn, then move (row vectors, like bones)
	public Matrix3x2 Transform =>
		Matrix3x2.CreateScale(Scale) * Matrix3x2.CreateRotation(Rotation) * Matrix3x2.CreateTranslation(Position);

	public Animation? FindAnimation(string? name) =>
		name == null ? null : Animations.Find(a => a.Name == name);

	// Clips whose animation still exists (deleting an animation leaves its clips waiting, so an
	// undo brings them back)
	public IEnumerable<SceneClip> LiveClips => Clips.Where(c => Animations.Contains(c.Animation));

	// The clip deciding the pose at a scene time: the one playing then; between clips, the last
	// one that ended (on its last frame); before the first, the first one (on its first frame).
	// Null when the object has no clips.
	public SceneClip? ClipAt(float time)
	{
		SceneClip? playing = null, ended = null, first = null;
		foreach (var c in LiveClips)
		{
			if (c.Covers(time) && (playing == null || c.Start >= playing.Start)) playing = c;
			if (c.End <= time && (ended == null || c.End > ended.End)) ended = c;
			if (first == null || c.Start < first.Start) first = c;
		}
		return playing ?? ended ?? first;
	}
}
