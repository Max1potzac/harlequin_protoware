using System.Numerics;

namespace Animator.Core.Editing;

// The scene's objects (which ones, in what order), each one's place, settings and clips, and the
// scene timeline's settings. Like StructureSnapshot, it restores the SAME objects and clips, so
// other undo steps that point at them keep working. (An object's rig and animations have their
// own snapshots.)
public sealed class SceneSnapshot
{
	private sealed record ClipState(SceneClip Clip, Animation Animation, float Start, float Length, float Offset, bool Loop, float MixIn);

	private sealed record ObjectState(
		SceneObject Object, string Name, ObjectKind Kind, Vector2 Position, float Rotation, Vector2 Scale,
		bool Visible, bool Locked, bool Export, bool Onion, string? Preview, string? AttachObject, string? AttachBone,
		List<ClipState> Clips);

	private readonly Scene _scene;
	private readonly List<ObjectState> _objects;
	private readonly float _duration;
	private readonly bool _loop;
	private readonly int _stageWidth, _stageHeight;
	private readonly ProjectKind _kind;

	private SceneSnapshot(Scene scene)
	{
		_scene = scene;
		_objects = scene.Objects.Select(o => new ObjectState(
			o, o.Name, o.Kind, o.Position, o.Rotation, o.Scale,
			o.Visible, o.Locked, o.Export, o.Onion, o.PreviewAnimation, o.AttachObject, o.AttachBone,
			o.Clips.Select(c => new ClipState(c, c.Animation, c.Start, c.Length, c.Offset, c.Loop, c.MixIn)).ToList())).ToList();
		_duration = scene.Duration;
		_loop = scene.Loop;
		_stageWidth = scene.StageWidth;
		_stageHeight = scene.StageHeight;
		_kind = scene.Kind;
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

			o.Clips.Clear();
			foreach (var c in s.Clips)
			{
				c.Clip.Animation = c.Animation;
				c.Clip.Start = c.Start;
				c.Clip.Length = c.Length;
				c.Clip.Offset = c.Offset;
				c.Clip.Loop = c.Loop;
				c.Clip.MixIn = c.MixIn;
				o.Clips.Add(c.Clip);
			}
			_scene.Objects.Add(o);
		}
		_scene.Duration = _duration;
		_scene.Loop = _loop;
		_scene.StageWidth = _stageWidth;
		_scene.StageHeight = _stageHeight;
		_scene.Kind = _kind;
	}
}
