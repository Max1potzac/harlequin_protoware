namespace Animator.Tests;

// Milestone 12.5: cutting an animation into animation sequences. A run of frames copied out or
// split off into an animation of its own, and the original closing the gap, every frame looking
// exactly as it did (eased, straight and stepped keys, drawings, events).
public class AnimationOpsTests
{
	private const int Fps = 30;

	// A bone turning and moving (straight, eased and stepped keys) and a hand swapping drawings
	// (before its first key it shows its rest drawing), with two events: 40 frames
	private static (Skeleton Skeleton, Animation Animation) Waving()
	{
		var skeleton = new Skeleton();
		var arm = new Bone { Name = "arm", Length = 50 };
		skeleton.Roots.Add(arm);
		skeleton.Slots.Add(new Slot("hand", arm) { SetupAttachment = "rest", ActiveAttachment = "rest" });

		float T(int frame) => frame / (float)Fps;
		var animation = new Animation { Name = "wave", Duration = T(40) };
		var turn = animation.GetOrCreate("arm", n => new RotateTimeline(n)).Track;
		turn.Add(T(0), 0f, CurveType.Linear);
		turn.Add(T(10), 1f, CurveType.Smooth);
		turn.Add(T(25), 0.5f, CurveType.Stepped);
		turn.Add(T(32), 2f, CurveType.Linear);
		turn.Add(T(40), 0f, CurveType.Linear);
		var move = animation.GetOrCreate("arm", n => new TranslateTimeline(n)).Track;
		move.Add(T(5), new Vector2(10, 0), CurveType.Smooth);
		move.Add(T(35), new Vector2(40, 20), CurveType.Linear);
		var hand = animation.GetOrCreate("hand", n => new AttachmentTimeline(n));
		hand.Add(T(8), "open");
		hand.Add(T(18), "fist");
		hand.Add(T(30), null);   // hidden
		var events = animation.GetOrCreateEvents();
		events.Add(T(12), new AnimationEvent("step"));
		events.Add(T(22), new AnimationEvent("clap"));
		return (skeleton, animation);
	}

	// What's showing at a frame
	private static (float Turn, Vector2 Move, string? Drawing) Pose(Skeleton skeleton, Animation animation, int frame)
	{
		skeleton.SetToSetupPose();
		animation.Apply(skeleton, frame / (float)Fps);
		var arm = skeleton.FindBone("arm")!;
		return (arm.Rotation, arm.Position, skeleton.FindSlot("hand")!.ActiveAttachment);
	}

	private static void Same(Skeleton skeleton, Animation expected, int expectedFrame, Animation actual, int actualFrame)
	{
		var want = Pose(skeleton, expected, expectedFrame);
		var got = Pose(skeleton, actual, actualFrame);
		string where = $"frame {actualFrame} of {actual.Name} (frame {expectedFrame} of {expected.Name})";
		Assert.True(MathF.Abs(want.Turn - got.Turn) < 1e-4f, $"turn at {where}: {got.Turn}, not {want.Turn}");
		Assert.True(Vector2.Distance(want.Move, got.Move) < 1e-3f, $"move at {where}: {got.Move}, not {want.Move}");
		Assert.True(want.Drawing == got.Drawing, $"drawing at {where}: {got.Drawing ?? "none"}, not {want.Drawing ?? "none"}");
	}

	private static int[] EventFrames(Animation animation) =>
		animation.Timelines.OfType<EventTimeline>().SelectMany(e => e.Track.Keys).Select(k => (int)MathF.Round(k.Time * Fps)).ToArray();

	private static int Frames(Animation animation) => (int)MathF.Round(animation.Duration * Fps);

	[Fact]
	public void Frames_copied_out_play_as_they_did()
	{
		var (skeleton, wave) = Waving();
		var piece = AnimationOps.Extract(wave, 15, 27, Fps, "middle");
		Assert.Equal("middle", piece.Name);
		Assert.Equal(13, Frames(piece));
		for (int f = 0; f <= 12; f++) Same(skeleton, wave, 15 + f, piece, f);
		Assert.Equal(new[] { 7 }, EventFrames(piece));   // the clap: 22 - 15
		Assert.Equal(40, Frames(wave));                    // the original is as it was

		// From the start: the hand shows its rest drawing until its first key, as before
		var start = AnimationOps.Extract(wave, 0, 9, Fps, "start");
		for (int f = 0; f <= 9; f++) Same(skeleton, wave, f, start, f);
		Assert.Equal("rest", Pose(skeleton, start, 3).Drawing);
		Assert.Equal(Array.Empty<int>(), EventFrames(start));
	}

	[Fact]
	public void Frames_cut_out_close_the_gap_and_the_rest_plays_as_it_did()
	{
		var (skeleton, original) = Waving();

		// From the middle: frames 15-27 go, 28-39 move up to 15-26
		var (_, cut) = Waving();
		AnimationOps.Cut(cut, 15, 27, Fps);
		Assert.Equal(27, Frames(cut));
		for (int f = 0; f <= 14; f++) Same(skeleton, original, f, cut, f);
		for (int f = 15; f <= 26; f++) Same(skeleton, original, f + 13, cut, f);
		Assert.Equal(new[] { 12 }, EventFrames(cut));   // the step stays, the clap went with its frames

		// From the start, and to the end
		var (_, head) = Waving();
		AnimationOps.Cut(head, 0, 9, Fps);
		for (int f = 0; f < 30; f++) Same(skeleton, original, f + 10, head, f);
		var (_, tail) = Waving();
		AnimationOps.Cut(tail, 30, 39, Fps);
		Assert.Equal(30, Frames(tail));
		for (int f = 0; f < 30; f++) Same(skeleton, original, f, tail, f);
	}

	[Fact]
	public void Splitting_takes_the_frames_into_a_sequence_of_their_own()
	{
		var (skeleton, original) = Waving();
		var (_, wave) = Waving();
		var piece = AnimationOps.Split(wave, 8, 21, Fps, "middle");
		Assert.Equal(14, Frames(piece));
		Assert.Equal(26, Frames(wave));
		for (int f = 0; f <= 13; f++) Same(skeleton, original, 8 + f, piece, f);
		for (int f = 0; f <= 7; f++) Same(skeleton, original, f, wave, f);
		for (int f = 8; f <= 25; f++) Same(skeleton, original, f + 14, wave, f);
		Assert.Equal(new[] { 4 }, EventFrames(piece));   // the step: 12 - 8
		Assert.Equal(new[] { 8 }, EventFrames(wave));    // the clap: 22 - 14

		// A straight stretch cut through gets one key at each new end, not one on every frame
		var straight = new Animation { Name = "line", Duration = 2f };
		var track = straight.GetOrCreate("arm", n => new RotateTimeline(n)).Track;
		track.Add(0f, 0f, CurveType.Linear);
		track.Add(2f, 6f, CurveType.Linear);
		var bit = AnimationOps.Extract(straight, 10, 40, Fps, "bit");
		Assert.Equal(2, bit.Find<RotateTimeline>("arm")!.Track.Keys.Count);
	}

	// Keys an import or a changed frame rate left between frames (a turn eased from frame 6.5, a
	// hit at 9.5) go with the frame before them: cut out with it, or kept with it
	private static (Skeleton Skeleton, Animation Animation) Between()
	{
		var (skeleton, _) = Waving();
		var animation = new Animation { Name = "odd", Duration = 20f / Fps };
		var turn = animation.GetOrCreate("arm", n => new RotateTimeline(n)).Track;
		turn.Add(0f, 0f, CurveType.Linear);
		turn.Add(6.5f / Fps, 1f, CurveType.Smooth);
		turn.Add(12.5f / Fps, 3f, CurveType.Linear);
		turn.Add(20f / Fps, 0f, CurveType.Linear);
		animation.GetOrCreateEvents().Add(9.5f / Fps, new AnimationEvent("hit"));
		return (skeleton, animation);
	}

	[Fact]
	public void Keys_between_frames_go_with_the_frame_before_them()
	{
		var (skeleton, original) = Between();

		// Frames 7-9 split off: the hit (while frame 9 shows) goes with them, once
		var (_, odd) = Between();
		var piece = AnimationOps.Split(odd, 7, 9, Fps, "bit");
		for (int f = 0; f <= 2; f++) Same(skeleton, original, 7 + f, piece, f);
		for (int f = 0; f <= 6; f++) Same(skeleton, original, f, odd, f);
		for (int f = 7; f <= 17; f++) Same(skeleton, original, f + 3, odd, f);
		Assert.Single(piece.Events!.Track.Keys);
		Assert.Null(odd.Events);

		// Frames 0-6 cut: the key at 6.5 goes with frame 6, and nothing lands in front of frame 0
		var (_, head) = Between();
		AnimationOps.Cut(head, 0, 6, Fps);
		Assert.Equal(13, Frames(head));
		for (int f = 0; f <= 13; f++) Same(skeleton, original, f + 7, head, f);
	}
}
