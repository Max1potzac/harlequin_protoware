using System.Windows.Input;
using Animator.Viewer;
using Animator.Viewer.Panels;
using Rig = Animator.Tests.PickingTests.Rig;

namespace Animator.Tests;

// Step 16: the dopesheet's tools, auto key (posing without keys until S) and playback options
public class TimelineToolsTests
{
	private const int Fps = 30;

	private static RotateTimeline Turns(string bone, params (int Frame, float Value)[] keys)
	{
		var tl = new RotateTimeline(bone);
		foreach (var (frame, value) in keys) tl.Track.Add(frame / (float)Fps, value);
		return tl;
	}

	private static int[] Frames(Timeline tl) => tl.KeyTimes.Select(t => KeyTimeOps.FrameOf(t, Fps)).ToArray();

	private static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = 1e-3f) =>
		Assert.True(Vector2.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

	// ======================= moving keys in time =======================

	[Fact]
	public void Nudged_keys_move_together_and_stay_inside_the_animation()
	{
		var a = Turns("a", (0, 0f), (10, 1f), (20, 2f));
		var moved = KeyTimeOps.Nudge(new[] { (a as Timeline, 10), (a, 20) }, +3, last: 30, Fps);
		Assert.Equal(new[] { 0, 13, 23 }, Frames(a));
		Assert.Equal(new[] { 13, 23 }, moved.Select(k => k.Frame).OrderBy(f => f));

		KeyTimeOps.Nudge(new[] { (a as Timeline, 13), (a, 23) }, +20, last: 30, Fps);   // only as far as the end
		Assert.Equal(new[] { 0, 20, 30 }, Frames(a));
	}

	[Fact]
	public void Keys_stretch_from_the_first_one_and_reverse_between_the_first_and_last()
	{
		var a = Turns("a", (4, 0f), (6, 1f), (10, 2f));
		var keys = new[] { (a as Timeline, 4), (a, 6), (a, 10) };
		var stretched = KeyTimeOps.Stretch(keys, around: 4, factor: 2f, last: 60, Fps);
		Assert.Equal(new[] { 4, 8, 16 }, Frames(a));

		KeyTimeOps.Reverse(stretched, Fps);
		Assert.Equal(new[] { 4, 12, 16 }, Frames(a));
		Assert.Equal(new[] { 2f, 1f, 0f }, a.Track.Keys.Select(k => k.Value));
	}

	[Fact]
	public void Frames_inserted_or_removed_move_the_keys_after_them_and_change_the_length()
	{
		var animation = new Animation { Name = "walk", Duration = 1f };
		var a = Turns("a", (0, 0f), (5, 1f), (10, 2f));
		animation.Timelines.Add(a);

		KeyTimeOps.InsertFrames(animation, at: 5, count: 3, Fps);   // the key at 5 stays: it lasts longer
		Assert.Equal(new[] { 0, 5, 13 }, Frames(a));
		Assert.Equal(33, KeyTimeOps.FrameOf(animation.Duration, Fps));

		Assert.Equal(2, KeyTimeOps.RemoveFrames(animation, at: 6, count: 2, Fps));
		Assert.Equal(new[] { 0, 5, 11 }, Frames(a));
		Assert.Equal(31, KeyTimeOps.FrameOf(animation.Duration, Fps));
		Assert.Equal(0, KeyTimeOps.RemoveFrames(animation, at: 40, count: 1, Fps));   // past the end: nothing
	}

	// ======================= the timeline panel =======================

	// A panel laid out as it is before it's first drawn: rows 20 high under a 24-high ruler, from
	// y = 24; frame f at x = 160 + 10 f
	private static (EditorState State, TimelinePanel Panel, RotateTimeline A, RotateTimeline B) Panel()
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "a" });
		skeleton.Roots[0].AddChild(new Bone { Name = "b" });
		var animation = new Animation { Name = "walk", Duration = 2f };
		var a = Turns("a", (0, 0f), (5, 1f), (10, 2f));
		var b = Turns("b", (5, 0f), (20, 1f));
		animation.Timelines.AddRange(new Timeline[] { a, b });
		var state = new EditorState(skeleton, animation) { Playing = false, AnimateMode = true };
		return (state, new TimelinePanel(state), a, b);
	}

	private static Vector2 At(int frame, int row) => new(160 + frame * 10, 24 + row * 20 + 10);

	[Fact]
	public void Ctrl_drag_boxes_keys_and_Alt_click_takes_every_key_at_its_frame()
	{
		var (state, panel, a, b) = Panel();
		panel.MouseDown(At(3, 0) + new Vector2(0, -8), ModifierKeys.Control);
		panel.MouseMove(At(7, 1) + new Vector2(0, 8));
		panel.MouseUp();
		Assert.Equal(new[] { ("a", 5), ("b", 5) }, state.SelectedKeys.Select(k => (k.Tl.Target, k.Frame)).OrderBy(k => k.Item1));

		panel.MouseDown(At(10, 0), ModifierKeys.Alt);   // the key at 10: just a has one there
		panel.MouseUp();
		Assert.Equal(new (Timeline, int)[] { (a, 10) }, state.SelectedKeys);
		panel.MouseDown(At(5, 1), ModifierKeys.Alt | ModifierKeys.Shift);   // and every key at 5, as well
		panel.MouseUp();
		Assert.Equal(3, state.SelectedKeys.Count);
	}

	[Fact]
	public void Shift_arrows_go_from_key_to_key_and_the_rows_can_be_just_the_picked_ones()
	{
		var (state, panel, _, b) = Panel();
		state.SetFrame(6);
		Assert.True(panel.JumpToKey(+1));
		Assert.Equal(10, state.Frame);
		Assert.True(panel.JumpToKey(+1));
		Assert.Equal(20, state.Frame);
		Assert.False(panel.JumpToKey(+1));
		Assert.True(panel.JumpToKey(-1));
		Assert.Equal(10, state.Frame);

		// Only b's row: it's the first row now, so a click there takes its key
		state.Pick(new object[] { state.Skeleton.FindBone("b")! });
		panel.OnlyPicked = true;
		panel.MouseDown(At(20, 0), ModifierKeys.None);
		panel.MouseUp();
		Assert.Equal(new (Timeline, int)[] { (b, 20) }, state.SelectedKeys);
	}

	[Fact]
	public void The_panels_frame_tools_are_one_undo_step_each()
	{
		var (state, panel, a, _) = Panel();
		state.SelectedKeys.Add((a, 5));
		state.SelectedKeys.Add((a, 10));
		Assert.True(panel.Nudge(+2));
		Assert.Equal(new[] { 0, 7, 12 }, Frames(a));
		Assert.Contains((a as Timeline, 12), state.SelectedKeys);   // the selection went along

		panel.InsertFrames(at: 0, count: 2);
		Assert.Equal(new[] { 0, 9, 14 }, Frames(a));
		Assert.Equal(62, state.FrameCount);

		state.Undo.Undo();
		state.Undo.Undo();
		Assert.Equal(new[] { 0, 5, 10 }, Frames(a));
		Assert.Equal(60, state.FrameCount);
	}

	// ======================= auto key =======================

	// What the editor does every frame, held changes included
	private static void Pose(Rig rig)
	{
		rig.Skeleton.SetToSetupPose();
		rig.Animation.Apply(rig.Skeleton, rig.State.Time);
		rig.State.Hold.Apply(rig.Skeleton, rig.Animation, rig.State.Frame);
		rig.Skeleton.UpdatePose();
	}

	// A press on the upper arm, wherever it points now, and round its joint (0, 100)
	private static void TurnUpperArm(Rig rig, float degrees)
	{
		float from = rig.Upper.Rotation;
		rig.Tool.MouseDown(Rig.Screen(30 * MathF.Cos(from), 100 + 30 * MathF.Sin(from)), MouseButton.Right);
		foreach (var t in new[] { 0.5f, 1f })
		{
			float a = from + degrees * t * MathF.PI / 180;
			rig.Tool.MouseMove(Rig.Screen(30 * MathF.Cos(a), 100 + 30 * MathF.Sin(a)));
			Pose(rig);
		}
		rig.Tool.MouseUp();
		Pose(rig);
	}

	[Fact]
	public void With_auto_key_off_posing_sets_no_key_until_it_is_keyed()
	{
		var rig = new Rig();
		rig.State.AnimateMode = true;
		rig.State.AutoKey = false;
		rig.State.SetFrame(10);
		Pose(rig);

		TurnUpperArm(rig, 40);
		Assert.Empty(rig.Animation.Timelines);              // no key...
		Assert.Null(rig.State.Undo.NextUndoName);           // ...and nothing to undo
		Assert.Equal(40 * MathF.PI / 180, rig.Upper.Rotation, 3);   // but posed, and it stays posed
		Assert.True(rig.State.Hold.Holds(rig.Upper));

		rig.State.Hold.KeyInto(rig.Animation, rig.State.Time);
		var key = Assert.Single(rig.Animation.Find<RotateTimeline>("upper")!.Track.Keys);
		Assert.Equal(10, KeyTimeOps.FrameOf(key.Time, Fps));
		Assert.Equal(40 * MathF.PI / 180, key.Value, 3);
		Assert.Null(rig.Animation.Find<TranslateTimeline>("upper"));   // only what changed gets a key
	}

	[Fact]
	public void Held_changes_build_up_on_their_frame_and_go_on_another()
	{
		var rig = new Rig();
		rig.State.AnimateMode = true;
		rig.State.AutoKey = false;
		Pose(rig);
		TurnUpperArm(rig, 30);
		TurnUpperArm(rig, 30);   // the second turn starts from where the first left it
		Assert.Equal(60 * MathF.PI / 180, rig.Upper.Rotation, 2);

		var joint = rig.Lower.World.Translation;
		rig.Drag(joint.X, joint.Y, joint.X + 10, joint.Y + 10, MouseButton.Right);   // the forearm's joint, moved
		Pose(rig);
		AssertNear(joint + new Vector2(10, 10), rig.Lower.World.Translation, 0.05f);
		Assert.True(rig.State.Hold.Holds(rig.Lower));
		Assert.True(rig.State.Hold.Holds(rig.Upper));
		Assert.Equal(60 * MathF.PI / 180, rig.Upper.Rotation, 2);

		rig.State.SetFrame(5);
		rig.State.Hold.KeepOnlyAt(rig.Skeleton, rig.Animation, rig.State.Frame, stillPosing: true);
		Pose(rig);
		Assert.False(rig.State.Hold.Any);
		Assert.Equal(0f, rig.Upper.Rotation, 4);
	}

	[Fact]
	public void Keying_with_nothing_held_keys_the_picked_whole_pose()
	{
		var rig = new Rig();
		rig.State.SetFrame(3);
		rig.Body.Offset = new Vector2(5, 6);
		int keyed = PoseHold.KeyNow(rig.Animation, rig.State.Time, new[] { rig.Upper }, new[] { rig.Body });
		Assert.Equal(2, keyed);
		Assert.NotNull(rig.Animation.Find<TranslateTimeline>("upper"));
		Assert.NotNull(rig.Animation.Find<RotateTimeline>("upper"));
		Assert.NotNull(rig.Animation.Find<ScaleTimeline>("upper"));
		AssertNear(new Vector2(5, 6), Assert.Single(rig.Animation.Find<PartTranslateTimeline>("body")!.Track.Keys).Value);
	}

	// ======================= playback =======================

	private static EditorState Player(bool loop)
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "root" });
		return new EditorState(skeleton, new Animation { Name = "a", Duration = 1f, Loop = loop }) { Fps = Fps };
	}

	[Fact]
	public void Speed_scales_how_far_play_goes()
	{
		var state = Player(loop: true);
		state.Speed = 0.5f;
		state.Advance(0.2f);
		Assert.Equal(0.1f, state.Time, 4);
	}

	[Fact]
	public void Ping_pong_turns_round_at_the_ends()
	{
		var state = Player(loop: false);
		state.PingPong = true;
		state.Time = 0.9f;
		var passed = state.Advance(0.3f);   // to the end and 0.2 back
		Assert.Equal(0.8f, state.Time, 4);
		Assert.True(state.PlayingBackwards);
		Assert.True(state.Playing);
		Assert.Contains(passed, span => MathF.Abs(span.From - 0.9f) < 1e-4f && MathF.Abs(span.To - 1f) < 1e-4f);

		state.Advance(0.9f);   // back past the start and 0.1 on
		Assert.Equal(0.1f, state.Time, 4);
		Assert.False(state.PlayingBackwards);
	}

	[Fact]
	public void The_picked_frames_play_over_and_over()
	{
		var state = Player(loop: false);
		state.PlayRange = (10, 14);   // frames 10..14: from 1/3 s to 0.5 s
		state.Advance(0.01f);         // outside them: it starts at the first
		Assert.Equal(10 / 30f + 0.01f, state.Time, 4);
		state.Time = 14.5f / 30;
		state.Advance(1 / 30f);       // past the last picked frame: round to the first again
		Assert.Equal(10.5f / 30, state.Time, 4);
		Assert.True(state.Playing);
	}
}
