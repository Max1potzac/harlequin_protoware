using System.Numerics;
using Animator.Viewer;

namespace Animator.Tests;

// The keys of the animation being edited, through the engine's KeyEditor (what the timeline,
// its menus and the keys' shortcuts call)
public class KeyEditorTests
{
	private const int Fps = 30;

	// An arm turning: keys at frames 0, 10 and 20 of a 30-frame animation
	private static (EditorState State, KeyEditor Keys, Bone Arm, RotateTimeline Turn) Rig()
	{
		var skeleton = new Skeleton();
		var arm = new Bone { Name = "arm", Length = 30 };
		skeleton.Roots.Add(arm);
		var walk = new Animation { Name = "walk", Duration = 1f, Loop = true };
		var turn = new RotateTimeline("arm");
		foreach (int f in new[] { 0, 10, 20 }) turn.Track.Add(f / (float)Fps, f / 10f, CurveType.Linear);
		walk.Timelines.Add(turn);
		var state = new EditorState(skeleton, walk) { Fps = Fps };
		return (state, new KeyEditor(state), arm, turn);
	}

	private static int[] Frames(Timeline tl) => tl.KeyTimes.Select(t => (int)MathF.Round(t * Fps)).ToArray();

	[Fact]
	public void Selected_keys_are_nudged_stretched_reversed_and_deleted_as_undo_steps()
	{
		var (state, keys, arm, turn) = Rig();
		Assert.Equal(3, keys.SelectKeysOf(new[] { arm }, Array.Empty<Slot>()));
		Assert.Equal(CurveType.Linear, keys.SelectedCurve());
		Assert.True(keys.SetCurve(CurveType.Stepped));
		Assert.Equal(CurveType.Stepped, keys.SelectedCurve());

		Assert.Equal(2, keys.SelectKeys(new[] { turn }, 10, fromOn: true));
		Assert.True(keys.Nudge(+2));
		Assert.Equal(new[] { 0, 12, 22 }, Frames(turn));
		Assert.True(keys.Stretch(0.5f));                    // from frame 12: 12, 17
		Assert.Equal(new[] { 0, 12, 17 }, Frames(turn));
		state.Undo.Undo();
		state.Undo.Undo();
		Assert.Equal(new[] { 0, 10, 20 }, Frames(turn));

		keys.SelectKeys(new[] { turn }, 0, fromOn: true);
		Assert.True(keys.Reverse());
		Assert.Equal(2f, turn.Track.Keys[0].Value, 4);      // the last value first now
		Assert.True(keys.DeleteSelected());
		Assert.Empty(turn.KeyTimes);
		Assert.Empty(state.SelectedKeys);
		state.Undo.Undo();
		Assert.Equal(3, turn.KeyTimes.Count());
	}

	[Fact]
	public void Keys_are_copied_and_pasted_at_the_playhead()
	{
		var (state, keys, _, turn) = Rig();
		keys.SelectKeys(new[] { turn }, 10, fromOn: true);
		Assert.True(keys.Copy());
		state.SetFrame(25);
		Assert.Equal(1, keys.Paste());                       // frame 35 is past the end: left out
		Assert.Equal(new[] { 0, 10, 20, 25 }, Frames(turn));
		Assert.Single(state.SelectedKeys);
	}

	[Fact]
	public void Frames_are_inserted_and_removed_and_the_playhead_jumps_between_keys()
	{
		var (state, keys, _, turn) = Rig();
		Assert.True(keys.InsertFrames(5, 2));
		Assert.Equal(new[] { 0, 12, 22 }, Frames(turn));
		Assert.Equal(32, state.FrameCount);
		Assert.True(keys.RemoveFrames(0, 2));
		Assert.Equal(30, state.FrameCount);

		var frames = Frames(turn);
		Assert.True(frames.Length >= 2);
		state.SetFrame(frames[^1]);
		Assert.True(keys.JumpToKey(new[] { turn }, -1));
		Assert.Equal(frames[^2], state.Frame);
		Assert.True(keys.JumpToKey(new[] { turn }, +1));
		Assert.Equal(frames[^1], state.Frame);
		Assert.False(keys.JumpToKey(new[] { turn }, +1));   // none after the last
	}

	[Fact]
	public void The_pose_is_keyed_held_or_its_keys_deleted_at_the_playhead()
	{
		var (state, keys, arm, turn) = Rig();
		state.SetFrame(5);
		Assert.Equal(0, keys.KeyPose());                     // nothing posed or picked
		state.Pick(new object[] { arm }, arm);
		Assert.True(keys.KeyPose() > 0);
		Assert.Contains(5, Frames(turn));

		state.AutoKey = false;
		Assert.True(keys.HoldInsteadOfKey(() => arm.Rotation = 1.5f));
		Assert.True(state.Hold.Any);
		state.AutoKey = true;
		Assert.False(keys.HoldInsteadOfKey(() => { }));

		Assert.True(keys.DeletePickedKeysHere());
		Assert.DoesNotContain(5, Frames(turn));
	}
}
