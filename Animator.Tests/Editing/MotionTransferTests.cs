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
}
