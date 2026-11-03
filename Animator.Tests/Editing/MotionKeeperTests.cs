namespace Animator.Tests;

// Rigging an animated object without spoiling its animations (MotionKeeper)
public class MotionKeeperTests
{
	private static Slot Part(Skeleton s, string name, Bone bone, Vector2 offset)
	{
		var asset = new Asset { Name = name, Width = 20, Height = 10 };
		asset.Layers.Add(new AssetLayer { Name = "Base" });
		s.Assets[asset.Id] = asset;
		var slot = new Slot(name, bone) { SetupAttachment = name };
		s.Slots.Add(slot);
		s.DefaultSkin.Set(name, new Attachment { Name = name, AssetId = asset.Id, Offset = offset });
		slot.ResetToSetup();
		return slot;
	}

	private static Matrix3x2 Shown(Skeleton s, Slot slot)
	{
		var d = s.ResolveAttachment(slot)!;
		return d.LocalMatrix(20, 10, slot.Offset, slot.Rotation, slot.Scale) * slot.Bone.World;
	}

	private static Matrix3x2 At(Skeleton s, Animation a, Slot slot, float t)
	{
		s.SetToSetupPose();
		a.Apply(s, t);
		s.UpdatePose();
		return Shown(s, slot);
	}

	private static void Same(Matrix3x2 a, Matrix3x2 b)
	{
		Assert.Equal(a.M31, b.M31, 1);
		Assert.Equal(a.M32, b.M32, 1);
		Assert.Equal(a.M11, b.M11, 3);
		Assert.Equal(a.M12, b.M12, 3);
	}

	// root → arm (turning over a second), a hand on the arm
	private static (Skeleton S, Bone Root, Bone Arm, Slot Hand, Animation Swing) Rig()
	{
		var s = new Skeleton();
		var root = new Bone { Name = "root", Length = 10 };
		var arm = new Bone { Name = "arm", Length = 40, SetupPosition = new Vector2(0, 30), SetupRotation = -0.5f };
		s.Roots.Add(root);
		root.AddChild(arm);
		var hand = Part(s, "hand", arm, new Vector2(35, 0));
		s.SetToSetupPose();
		s.UpdateWorld();
		var swing = new Animation { Name = "swing", Duration = 1f };
		var turn = new RotateTimeline("arm");
		turn.Track.Add(0, 0f);
		turn.Track.Add(1, 1.2f);
		swing.Timelines.Add(turn);
		var move = new TranslateTimeline("root");
		move.Track.Add(0, Vector2.Zero);
		move.Track.Add(1, new Vector2(20, 5));
		swing.Timelines.Add(move);
		return (s, root, arm, hand, swing);
	}

	[Fact]
	public void A_bone_turned_at_rest_with_its_parts_staying_put_leaves_the_animation_as_it_was()
	{
		var (s, _, arm, hand, swing) = Rig();
		var before = new[] { 0f, 0.4f, 1f }.Select(t => At(s, swing, hand, t)).ToList();
		var keeper = MotionKeeper.Capture(s, new[] { swing }, 30)!;

		var frozen = FrozenRest.Capture(s);
		RestEdits.Turn(frozen, arm, 0.7f);   // fitting the bone to the art: the hand stays put at rest
		RestEdits.MoveJoint(frozen, arm, new Vector2(5, 25), keepLength: true);
		Assert.True(keeper.Apply());

		var after = new[] { 0f, 0.4f, 1f }.Select(t => At(s, swing, hand, t)).ToList();
		for (int i = 0; i < 3; i++) Same(before[i], after[i]);
	}

	[Fact]
	public void A_part_put_on_another_bone_keeps_showing_where_it_did()
	{
		var (s, root, _, hand, swing) = Rig();
		var before = new[] { 0f, 0.5f, 1f }.Select(t => At(s, swing, hand, t)).ToList();
		var keeper = MotionKeeper.Capture(s, new[] { swing }, 30)!;
		PartOps.MoveToBone(s, hand, root);
		keeper.Apply();
		var after = new[] { 0f, 0.5f, 1f }.Select(t => At(s, swing, hand, t)).ToList();
		for (int i = 0; i < 3; i++) Same(before[i], after[i]);
		Assert.NotNull(swing.Find<PartRotateTimeline>("hand"));
	}

	[Fact]
	public void A_new_bone_takes_over_a_parts_motion()
	{
		var (s, root, _, hand, swing) = Rig();
		var before = new[] { 0f, 0.5f, 1f }.Select(t => At(s, swing, hand, t)).ToList();
		var keeper = MotionKeeper.Capture(s, new[] { swing }, 30)!;
		var traced = new Bone { Name = "traced", Length = 20, SetupPosition = new Vector2(10, 40) };
		root.AddChild(traced);
		PartOps.MoveToBone(s, hand, traced);
		keeper.FollowPart(traced, hand);

		var after = new[] { 0f, 0.5f, 1f }.Select(t => At(s, swing, hand, t)).ToList();
		for (int i = 0; i < 3; i++) Same(before[i], after[i]);
		Assert.NotNull(swing.Find<RotateTimeline>("traced"));
		Assert.Null(swing.Find<PartRotateTimeline>("hand"));   // the bone carries it now
		Assert.True(swing.Find<RotateTimeline>("traced")!.Track.Keys.Count < 31);   // (thinned out)
	}

	[Fact]
	public void Nothing_animated_nothing_to_keep()
	{
		var (s, _, _, _, _) = Rig();
		Assert.Null(MotionKeeper.Capture(s, new[] { new Animation { Name = "still", Duration = 1 } }, 30));
	}
}
