namespace Animator.Viewer;

// The scene level's working state, shared by the viewport (ObjectTool), the Objects panel and
// the scene timeline: the scene playhead, and what's selected
public class SceneState
{
	// The scene playhead, in seconds
	public float Time { get; set; }

	// The object the panels and the green handles work on. Picking another object drops the
	// clip selection (a selected clip always belongs to the selected object).
	public SceneObject? Selected
	{
		get => _selected;
		set
		{
			if (value != _selected) SelectedClip = null;
			_selected = value;
		}
	}
	private SceneObject? _selected;

	public SceneClip? SelectedClip { get; set; }
}
