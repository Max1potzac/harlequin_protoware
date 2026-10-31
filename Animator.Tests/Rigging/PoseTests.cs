using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Step 18: poses kept by name, put on again (partly, mirrored), and mirroring
public class PoseTests
{
	private static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = 1e-3f) =>
		Assert.True(Vector2.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

	[Theory]
	[InlineData("arm_L", "arm_R")]
	[InlineData("Left Arm", "Right Arm")]
	[InlineData("hand.r", "hand.l")]
	[InlineData("legRight", "legLeft")]
	[InlineData("upperArmL", "upperArmR")]
	[InlineData("LeftFoot", "RightFoot")]
	[InlineData("L_eye", "R_eye")]
	public void Names_that_say_a_side_have_another_side(string name, string other)
	{
		Assert.Equal(other, MirrorNames.Other(name));
		Assert.Equal(name, MirrorNames.Other(other));
	}

	[Theory]
	[InlineData("torso")]
	[InlineData("Ring")]
	[InlineData("hair")]
	[InlineData("Root")]
	public void Other_names_dont(string name) => Assert.Null(MirrorNames.Other(name));

	// A body standing up, arms out to each side: root at (0, 0) pointing up, arms at (±10, 50)
	private static (Skeleton Skeleton, Bone Root, Bone Left, Bone Right) Body()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 50, SetupRotation = MathF.PI / 2 };
		skeleton.Roots.Add(root);
		// In the root's space (x up, y left): the left arm at (50, 10) pointing left (+90° from up), the right one mirrored
		var left = new Bone { Name = "arm_L", Length = 30, SetupPosition = new Vector2(50, 10), SetupRotation = MathF.PI / 2 };
		var right = new Bone { Name = "arm_R", Length = 30, SetupPosition = new Vector2(50, -10), SetupRotation = -MathF.PI / 2 };
		root.AddChild(left);
		root.AddChild(right);
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		return (skeleton, root, left, right);
	}

	[Fact]
	public void Mirroring_swaps_the_sides_and_flips_whats_in_the_middle()
	{
		var (skeleton, root, left, right) = Body();
		AssertNear(new Vector2(-10, 50), left.World.Translation);   // (left is -x)
		left.Rotation += 0.5f;     // the left arm raised
		root.Rotation += 0.2f;     // the body leaning
		skeleton.UpdateWorld();
		var leftTip = left.TipWorld;

		PoseOps.Mirror(skeleton);

		// The body leans the other way; the right arm is raised as the left one was, mirrored
		Assert.Equal(MathF.PI / 2 - 0.2f, root.WorldRotation, 3);
		AssertNear(new Vector2(-leftTip.X, leftTip.Y), right.TipWorld, 0.01f);
		Assert.Equal(left.SetupRotation, left.Rotation, 3);   // and the left one as the right one was (at rest)
	}

	[Fact]
	public void A_kept_pose_goes_back_on_partly_or_mirrored_and_is_saved()
	{
		var (skeleton, _, left, right) = Body();
		left.Rotation = MathF.PI / 2 + 0.6f;
		var raised = PoseOps.Capture(skeleton, "wave", new[] { left });
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();

		PoseOps.Apply(skeleton, raised, 0.5f);
		Assert.Equal(MathF.PI / 2 + 0.3f, left.Rotation, 4);

		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		PoseOps.ApplyMirrored(skeleton, raised);
		Assert.Equal(-MathF.PI / 2 - 0.6f, right.Rotation, 3);   // the right arm waves
		Assert.Equal(MathF.PI / 2, left.Rotation, 4);           // the left one stays as it was

		skeleton.Poses.Add(raised);
		var dto = ProjectMapper.ToDto(skeleton, Array.Empty<Animation>(), 30);
		var (loaded, _) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(JsonSerializer.Serialize(dto, ProjectMapper.Json), ProjectMapper.Json)!);
		var pose = Assert.Single(loaded.Poses);
		Assert.Equal("wave", pose.Name);
		Assert.Equal(MathF.PI / 2 + 0.6f, pose.Bones["arm_L"].Rotation, 5);
	}

	[Fact]
	public void Symmetrizing_makes_the_other_side_the_mirror_image()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 50, SetupRotation = MathF.PI / 2 };
		skeleton.Roots.Add(root);
		var arm = new Bone { Name = "arm_L", Length = 30, SetupPosition = new Vector2(50, 10), SetupRotation = 2f };
		root.AddChild(arm);
		var hand = new Bone { Name = "hand_L", Length = 10, SetupPosition = new Vector2(30, 0), SetupRotation = 0.3f };
		arm.AddChild(hand);

		var made = PoseOps.Symmetrize(skeleton, new[] { arm, hand });
		Assert.Equal(new[] { "arm_R", "hand_R" }, made.Select(b => b.Name));
		var otherHand = skeleton.FindBone("hand_R")!;
		Assert.Same(skeleton.FindBone("arm_R"), otherHand.Parent);
		AssertNear(new Vector2(-hand.TipWorld.X, hand.TipWorld.Y), otherHand.TipWorld, 0.01f);
		AssertNear(new Vector2(-hand.World.Translation.X, hand.World.Translation.Y), otherHand.World.Translation, 0.01f);
	}
}
