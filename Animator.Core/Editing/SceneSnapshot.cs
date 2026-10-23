using System.Numerics;

namespace Animator.Core.Editing;

// The scene's objects (which ones, in what order) and each one's place and settings.
// Like StructureSnapshot, it restores the SAME objects, so other undo steps that point at an
// object's rig keep working. (An object's rig and animations have their own snapshots.)
public sealed class SceneSnapshot
{
	private sealed record ObjectState(
		SceneObject Object, string Name, ObjectKind Kind, Vector2 Position, float Rotation, Vector2 Scale,
		bool Visible, bool Locked, bool Export, bool Onion, string? Preview, string? AttachObject, string? AttachBone);

	private readonly Scene _scene;
	private readonly List<ObjectState> _objects;

	private SceneSnapshot(Scene scene)
	{
		_scene = scene;
		_objects = scene.Objects.Select(o => new ObjectState(
			o, o.Name, o.Kind, o.Position, o.Rotation, o.Scale,
			o.Visible, o.Locked, o.Export, o.Onion, o.PreviewAnimation, o.AttachObject, o.AttachBone)).ToList();
	}

	public static SceneSnapshot Capture(Scene scene) => new(scene);

	public void Restore()
	{
		_scene.Objects.Clear();
		foreach (var s in _objects)
		{
			var o = s.Object;
			o.Name = s.Name;
			o.Kind = s.Kind;
			o.Position = s.Position;
			o.Rotation = s.Rotation;
			o.Scale = s.Scale;
			o.Visible = s.Visible;
			o.Locked = s.Locked;
			o.Export = s.Export;
			o.Onion = s.Onion;
			o.PreviewAnimation = s.Preview;
			o.AttachObject = s.AttachObject;
			o.AttachBone = s.AttachBone;
			_scene.Objects.Add(o);
		}
	}
}
