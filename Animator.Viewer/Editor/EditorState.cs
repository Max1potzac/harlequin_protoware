using Animator.Core.Editing;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Viewer;

public class EditorState
{
	public Skeleton Skeleton { get; set; }   // the rig of the object being edited
	public Animation Animation { get; set; }
	public UndoStack Undo { get; } = new();
	// Every animation in the project. "Animation" is the one being edited.
	public List<Animation> Animations { get; set; } = new();
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
		Animations.Add(animation);
	}
	// Runs a structural rig edit as one undo step.
	// The change returns the step's name, or null if it ended up changing nothing.
	// The selected part (null = none). When set, Selected is that part's bone.
	public Slot? SelectedSlot { get; set; }

	public StructureSnapshot CaptureStructure() => StructureSnapshot.Capture(Skeleton, Animations);

	// For edits spread over time (like a drag): capture at the start, push at the end
	public void PushStructure(StructureSnapshot before, string name) =>
		Undo.Push(new SnapshotCommand<StructureSnapshot>(name, before, CaptureStructure(), s => s.Restore()));

	// One-shot structural edit. The change returns the step's name, or null if nothing changed.
	public bool EditStructure(Func<string?> change)
	{
		var before = CaptureStructure();
		string? name = change();
		if (name == null)
		{
			before.Restore();   // safety net: undo anything a change did before giving up
			return false;
		}
		PushStructure(before, name);
		return true;
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