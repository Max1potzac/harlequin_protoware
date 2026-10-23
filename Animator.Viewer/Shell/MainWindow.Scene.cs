namespace Animator.Viewer;

// The scene: every object in the project. One object is open for editing at a time; its
// rig lives in _skeleton and its animations in _state.Animations, which is what every
// panel and tool works on. (Milestone 9.5 step 1: the scene exists and is saved, with one
// object; the objects panel comes next.)
public partial class MainWindow
{
	private Scene _scene = new();
	private SceneObject? _active;   // the object open for editing

	// A fresh scene holding what's being edited now, as its only object
	private Scene SingleObjectScene()
	{
		var scene = new Scene { Fps = _state.Fps };
		var obj = new SceneObject("Character", ObjectKind.Rigged, _skeleton) { Animations = _state.Animations };
		scene.Objects.Add(obj);
		_active = obj;
		return scene;
	}

	// Opens an object for editing: the one open before gets its own copy of its rig back,
	// then this one's rig and animations move into the editing slots
	private void EditObject(SceneObject obj)
	{
		if (_active != null && _active != obj && _scene.Objects.Contains(_active))
		{
			var own = new Skeleton();
			own.ReplaceWith(_skeleton);
			_active.Skeleton = own;
			_active.Animations = _state.Animations.ToList();
		}

		_skeleton.ReplaceWith(obj.Skeleton);
		obj.Skeleton = _skeleton;
		_state.Animations.Clear();
		_state.Animations.AddRange(obj.Animations);
		if (_state.Animations.Count == 0)
			_state.Animations.Add(new Animation { Name = "animation", Duration = 2f, Loop = true });
		obj.Animations = _state.Animations;
		_state.Animation = _state.Animations[0];
		_active = obj;
	}
}
