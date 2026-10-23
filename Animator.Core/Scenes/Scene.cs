using System.Numerics;

namespace Animator.Core.Scenes;

// A project: objects drawn in list order (first = furthest back), all at one frame rate
public class Scene
{
	public List<SceneObject> Objects { get; } = new();
	public int Fps { get; set; } = 30;

	public SceneObject? Find(string name) => Objects.Find(o => o.Name == name);

	public string UniqueName(string baseName)
	{
		baseName = string.IsNullOrWhiteSpace(baseName) ? "object" : baseName.Trim();
		if (Find(baseName) == null) return baseName;
		for (int i = 2; ; i++)
		{
			string candidate = $"{baseName}{i}";
			if (Find(candidate) == null) return candidate;
		}
	}

	// The bone an object is attached to (null if it's free, or the bone no longer exists)
	public Bone? AttachmentBone(SceneObject obj) =>
		obj.AttachObject is { } holder && obj.AttachBone is { } bone
			? Find(holder)?.Skeleton.FindBone(bone)
			: null;

	// What an object's own position, rotation and scale are measured from: the scene, or the
	// (posed) bone it's attached to. Chains are followed a few levels deep at most, so a loop
	// of attachments can't hang the program.
	public Matrix3x2 ParentTransform(SceneObject obj) => ParentTransform(obj, 0);

	// Object space → scene, attachment included
	public Matrix3x2 WorldTransform(SceneObject obj) => WorldTransform(obj, 0);

	private const int MaxAttachDepth = 8;

	private Matrix3x2 ParentTransform(SceneObject obj, int depth) =>
		depth < MaxAttachDepth && AttachmentBone(obj) is { } bone && Find(obj.AttachObject!) is { } holder && holder != obj
			? bone.World * WorldTransform(holder, depth + 1)
			: Matrix3x2.Identity;

	private Matrix3x2 WorldTransform(SceneObject obj, int depth) => obj.Transform * ParentTransform(obj, depth);
}
