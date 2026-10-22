using Animator.Core.Editing;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Viewer;

public class EditorState
{
	public Skeleton Skeleton { get; }
	public Animation Animation { get; set; }
	public UndoStack Undo { get; } = new();

	public Bone? Selected { get; set; }
	public bool AnimateMode { get; set; }
	public bool Playing { get; set; } = true;
	public int Fps { get; set; } = 30;
	public float Time { get; set; }
	// Selected keys, identified by (timeline, frame)
	public HashSet<(Timeline Tl, int Frame)> SelectedKeys { get; } = new();

	public EditorState(Skeleton skeleton, Animation animation)
	{
		Skeleton = skeleton;
		Animation = animation;
	}

	public int Frame => (int)MathF.Round(Time * Fps);
	public int FrameCount => (int)MathF.Round(Animation.Duration * Fps);

	// Keys are always placed at exact frame times, so the playhead and keys line up
	public void SetFrame(int frame) => Time = Math.Clamp(frame, 0, FrameCount) / (float)Fps;

	public void Advance(float dt)
	{
		float d = Animation.Duration;
		if (d <= 0) { Time = 0; return; }

		Time += dt;
		if (Time > d)
		{
			if (Animation.Loop) Time %= d;
			else { Time = d; Playing = false; }
		}
	}
}