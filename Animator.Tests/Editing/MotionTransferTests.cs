namespace Animator.Tests;

// Animations brought over by where the parts show (MotionTransfer): two rigs that don't match
// bone for bone, the same parts by name
public class MotionTransferTests
{
	private static void Part(Skeleton s, string name, Bone bone, Vector2 offset)
	{
		var asset = new Asset { Name = name, Width = 20, Height = 10 };
		asset.Layers.Add(new AssetLayer { Name = "Base" });
		s.Assets[asset.Id] = asset;
		s.Slots.Add(new Slot(name, bone) { SetupAttachment = name });
		s.DefaultSkin.Set(name, new Attachment { Name = name, AssetId = asset.Id, Offset = offset });
	}

	private static Matrix3x2 Shown(Skeleton s, string part)
	{
		var slot = s.FindSlot(part)!;
		var d = s.ResolveAttachment(slot)!;
		return d.LocalMatrix(20, 10, slot.Offset, slot.Rotation, slot.Scale) * slot.Bone.World;
	}

	[Fact]
	public void A_swing_on_their_bone_plays_here_where_the_part_has_no_bone_of_its_own()
	{
		// Theirs: an arm on a bone of its own, which swings. Ours: everything on one bone.
		var theirs = new Skeleton();
		var root = new Bone { Name = "p_root", Length = 10 };
		var upper = new Bone { Name = "p_upper", Length = 30, SetupPosition = new Vector2(0, 40) };
		theirs.Roots.Add(root);
		root.AddChild(upper);
		Part(theirs, "p_body", root, new Vector2(0, 0));
		Part(theirs, "p_arm", upper, new Vector2(5, 1));
		theirs.SetToSetupPose();
		theirs.UpdateWorld();
		var swing = new Animation { Name = "idle", Duration = 1f, Loop = true };
		var turn = new RotateTimeline("p_upper");
		turn.Track.Add(0, 0f);
		turn.Track.Add(1, 0.8f);
		swing.Timelines.Add(turn);
		var move = new TranslateTimeline("p_root");
		move.Track.Add(0, Vector2.Zero);
		move.Track.Add(1, new Vector2(10, -4));
		swing.Timelines.Add(move);

		var ours = new Skeleton();
		var only = new Bone { Name = "q_root", Length = 10 };
		ours.Roots.Add(only);
		Part(ours, "q_body", only, new Vector2(0, 0));
		Part(ours, "q_arm", only, new Vector2(5, 41));   // (where theirs shows at rest)
		ours.SetToSetupPose();
		ours.UpdateWorld();
		var ourAnimations = new List<Animation> { new() { Name = "idle", Duration = 1f, Loop = true } };

		var plan = ObjectMerge.Suggest(theirs, new[] { swing }, ours);
		plan.AsPlaced = true;
		plan.ByParts = true;
		plan.Fps = 10;
		plan.Suffix = "_p";
		var result = ObjectMerge.Merge(theirs, new[] { swing }, ours, ourAnimations, plan);
		Assert.Equal(1, result.Animations);
		var copy = ourAnimations.Single(a => a.Name == "idle_p");

		foreach (float t in new[] { 0f, 0.3f, 0.7f, 1f })
		{
			theirs.SetToSetupPose();
			swing.Apply(theirs, t);
			theirs.UpdatePose();
			ours.SetToSetupPose();
			copy.Apply(ours, t);
			ours.UpdatePose();
			foreach (var (a, b) in new[] { ("p_arm", "q_arm"), ("p_body", "q_body") })
			{
				var want = Shown(theirs, a);
				var got = Shown(ours, b);
				Assert.Equal(want.Translation.X, got.Translation.X, 2);
				Assert.Equal(want.Translation.Y, got.Translation.Y, 2);
				Assert.Equal(want.M11, got.M11, 3);
				Assert.Equal(want.M12, got.M12, 3);
			}
		}
	}

	// A rigid limb: their forearm slides along the arm as it turns (their art joined elsewhere);
	// here it keeps its elbow where it hangs from the arm, and only turns as theirs does
	[Fact]
	public void A_rigid_limb_keeps_its_joint_and_turns_as_theirs()
	{
		Skeleton Arm(string p, out Bone lower)
		{
			var s = new Skeleton();
			var root = new Bone { Name = p + "root", Length = 10 };
			var upper = new Bone { Name = p + "upper", Length = 30, SetupPosition = new Vector2(0, 40) };
			lower = new Bone { Name = p + "lower", Length = 30, SetupPosition = new Vector2(30, 0) };
			s.Roots.Add(root);
			root.AddChild(upper);
			upper.AddChild(lower);
			Part(s, p + "upper", upper, new Vector2(5, 0));
			Part(s, p + "lower", lower, new Vector2(5, 0));
			s.SetToSetupPose();
			s.UpdateWorld();
			return s;
		}
		var theirs = Arm("p_", out _);
		var swing = new Animation { Name = "eat", Duration = 1f };
		var turn = new RotateTimeline("p_lower");
		turn.Track.Add(0, 0f);
		turn.Track.Add(1, 0.6f);
		swing.Timelines.Add(turn);
		var slide = new TranslateTimeline("p_lower");
		slide.Track.Add(0, Vector2.Zero);
		slide.Track.Add(1, new Vector2(6, 3));
		swing.Timelines.Add(slide);
		var ours = Arm("q_", out var ourLower);
		var ourAnimations = new List<Animation>();

		var plan = ObjectMerge.Suggest(theirs, new[] { swing }, ours);
		plan.AsPlaced = true;
		plan.ByParts = true;
		plan.Fps = 10;
		plan.Suffix = "_p";
		plan.Rigid = part => part.Contains("lower");
		ObjectMerge.Merge(theirs, new[] { swing }, ours, ourAnimations, plan);
		var copy = ourAnimations.Single(a => a.Name == "eat_p");

		ours.SetToSetupPose();
		copy.Apply(ours, 1f);
		ours.UpdatePose();
		Assert.Equal(new Vector2(30, 0), ourLower.Position);   // (the elbow where it was)
		Assert.Equal(0.6f, ourLower.Rotation, 3);              // (turned as theirs)
	}

	// A skin's forearm, placed as theirs shows next to ours (their upper arm is longer: their
	// elbow is further out), fitted by its joint: moved back onto our elbow, which our forearm
	// turns about, so it stays on the arm as it turns
	[Fact]
	public void A_skins_limb_is_fitted_onto_our_joint()
	{
		Skeleton Arm(string p, float elbow, List<Animation> animations)
		{
			var s = new Skeleton();
			var root = new Bone { Name = p + "root", Length = 10 };
			var upper = new Bone { Name = p + "upper", Length = elbow, SetupPosition = new Vector2(0, 40) };
			var lower = new Bone { Name = p + "lower", Length = 30, SetupPosition = new Vector2(elbow, 0) };
			s.Roots.Add(root);
			root.AddChild(upper);
			upper.AddChild(lower);
			Part(s, p + "upper", upper, new Vector2(5, 0));
			Part(s, p + "lower", lower, new Vector2(5, 0));
			s.SetToSetupPose();
			s.UpdateWorld();
			var bend = new Animation { Name = "idle", Duration = 1f, Loop = true };
			var turn = new RotateTimeline(p + "lower");
			turn.Track.Add(0, 0f);
			turn.Track.Add(1, 0.9f);
			bend.Timelines.Add(turn);
			animations.Add(bend);
			return s;
		}
		var theirAnimations = new List<Animation>();
		var ourAnimations = new List<Animation>();
		var theirs = Arm("p_", 36, theirAnimations);
		var ours = Arm("q_", 30, ourAnimations);
		var symbols = new Dictionary<Attachment, Matrix3x2>();   // (each drawing's symbol: its bone's space)
		foreach (var sk in new[] { theirs, ours })
			foreach (var (_, d) in sk.DefaultSkin.Entries)
				symbols[d] = d.LocalMatrix(20, 10);

		var plan = ObjectMerge.Suggest(theirs, theirAnimations, ours);
		plan.AsPlaced = true;
		plan.Symbols = symbols;
		plan.SkinName = "long";
		plan.SkinParts.Add("p_lower");
		plan.ByPose = part => part.Contains("lower");
		plan.FitByJoints = part => part.Contains("lower");
		plan.Animations.Clear();
		var result = ObjectMerge.Merge(theirs, theirAnimations, ours, ourAnimations, plan);

		var forearm = result.Skin!.AttachmentsFor("q_lower").Single();
		Assert.Equal(5f, forearm.Offset.X, 1);   // (as placed: 11, 6 past our elbow)
		Assert.Equal(0f, forearm.Offset.Y, 1);
		Assert.Contains(result.Notes, n => n.Contains("fitted by their joints"));
	}

	[Fact]
	public void A_matrix_comes_apart_into_its_move_turn_and_scale()
	{
		var m = Matrix3x2.CreateScale(2, -0.5f) * Matrix3x2.CreateRotation(1.1f) * Matrix3x2.CreateTranslation(3, 4);
		var (position, rotation, scale) = MotionTransfer.Decompose(m);
		Assert.Equal(new Vector2(3, 4), position);
		Assert.Equal(1.1f, rotation, 4);
		Assert.Equal(2f, scale.X, 4);
		Assert.Equal(-0.5f, scale.Y, 4);
	}

	[Fact]
	public void A_part_that_would_swing_round_between_frames_is_held_there()
	{
		// A bone half-turning from one frame to the next, its part far from it: each frame is
		// right, but half way between them the part would be swung out of its way
		var s = new Skeleton();
		var bone = new Bone { Name = "b", Length = 10 };
		s.Roots.Add(bone);
		Part(s, "arm", bone, new Vector2(40, 0));
		var spin = new Animation { Name = "spin", Duration = 3 / 30f };
		var turn = new RotateTimeline("b");
		turn.Track.Add(0, 0f);
		turn.Track.Add(1 / 30f, 0f);
		turn.Track.Add(2 / 30f, MathF.PI);
		turn.Track.Add(3 / 30f, MathF.PI);
		spin.Timelines.Add(turn);

		Assert.Equal(1, JumpHolds.Hold(s, spin, 30));
		Assert.Equal(CurveType.Stepped, turn.Track.Keys[1].Curve);
		Assert.Equal(CurveType.Linear, turn.Track.Keys[0].Curve);
	}
}
