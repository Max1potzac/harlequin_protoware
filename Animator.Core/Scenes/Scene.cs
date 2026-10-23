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
}
