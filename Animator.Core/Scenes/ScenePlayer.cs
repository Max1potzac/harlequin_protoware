namespace Animator.Core.Scenes;

// Poses objects the way the scene shows them, without the editor (exporting uses this, and the
// editor for every object you aren't working on)
public static class ScenePlayer
{
	// Every object at a scene time
	public static void Pose(Scene scene, float sceneTime)
	{
		foreach (var obj in scene.Objects) Pose(obj, sceneTime);
	}

	// What an object shows at a scene time: its clip there (held before and after), or, with no
	// clips (or only clips whose animation was deleted), its preview animation looping
	public static void Pose(SceneObject obj, float sceneTime)
	{
		obj.Skeleton.SetToSetupPose();
		if (obj.ClipAt(sceneTime) is { } clip) clip.Animation.Apply(obj.Skeleton, clip.AnimationTime(sceneTime));
		else Preview(obj)?.Apply(obj.Skeleton, sceneTime);
		obj.Skeleton.UpdatePose();
	}

	// One of the object's animations at its own time
	public static void Pose(SceneObject obj, Animation animation, float time)
	{
		obj.Skeleton.SetToSetupPose();
		animation.Apply(obj.Skeleton, time);
		obj.Skeleton.UpdatePose();
	}

	// The animation an object loops when it has no clips
	public static Animation? Preview(SceneObject obj) =>
		obj.FindAnimation(obj.PreviewAnimation) ?? obj.Animations.FirstOrDefault();
}
