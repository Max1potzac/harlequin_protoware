using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Step 23: copy constraints (follow another bone's turn, place, size) and jiggle constraints
public class ConstraintTests
{
	private static void Near(Vector2 expected, Vector2 actual, float within = 1e-2f) =>
		Assert.True(Vector2.Distance(expected, actual) < within, $"expected {expected}, got {actual}");

	// Two root bones: a at the origin, b at (100, 0), both pointing right
	private static (Skeleton Skeleton, Bone A, Bone B) Pair()
	{
		var skeleton = new Skeleton();
		var a = new Bone { Name = "a", Length = 40 };
		var b = new Bone { Name = "b", Length = 40, SetupPosition = new Vector2(100, 0) };
		skeleton.Roots.Add(a);
		skeleton.Roots.Add(b);
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		return (skeleton, a, b);
	}

	private static CopyConstraint Follow(Skeleton skeleton, Bone bone, Bone source, bool local = false)
	{
		var c = new CopyConstraint { Name = $"{bone.Name}_follow", Bone = bone, Source = source, Local = local };
		c.KeepOffsets();
		skeleton.CopyConstraints.Add(c);
		return c;
	}

	private static void Pose(Skeleton skeleton, Action animate)
	{
		skeleton.SetToSetupPose();
		animate();
		skeleton.UpdatePose();
	}

	[Fact]
	public void Following_in_the_object_takes_the_other_bones_turn_and_place_by_the_mixes()
	{
		var (skeleton, a, b) = Pair();
		var c = Follow(skeleton, b, a);
		Pose(skeleton, () => a.Rotation = 0.5f);
		Assert.Equal(0.5f, b.WorldRotation, 4);
		Near(new Vector2(100, 0), b.World.Translation);   // its place: not followed (mix 0)

		c.MoveMix = 1f;
		Pose(skeleton, () => a.Rotation = MathF.PI / 2);
		Near(new Vector2(0, 100), b.World.Translation);   // carried round a, keeping its distance
		Assert.Equal(MathF.PI / 2, b.WorldRotation, 4);

		c.SetupMix = 0.5f;
		c.MoveMix = 0f;
		Pose(skeleton, () => a.Rotation = 0.5f);
		Assert.Equal(0.25f, b.WorldRotation, 4);   // half of it
	}

	[Fact]
	public void Following_locally_takes_how_the_other_bone_moves_from_its_rest()
	{
		var (skeleton, a, b) = Pair();
		b.SetupRotation = 1f;
		var c = Follow(skeleton, b, a, local: true);
		c.MoveMix = 1f;
		c.ScaleMix = 1f;
		Pose(skeleton, () =>
		{
			a.Rotation = 0.3f;
			a.Position = new Vector2(5, -5);
			a.Scale = new Vector2(2, 1);
		});
		Assert.Equal(1.3f, b.Rotation, 4);
		Near(new Vector2(105, -5), b.Position);
		Near(new Vector2(2, 1), b.Scale);
	}

	// A base bone and a tail on its end that jiggles; the base swung up a radian at once
	[Fact]
	public void A_jiggling_bone_lags_swings_past_and_settles_and_starts_again_when_time_jumps_back()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "base", Length = 50 };
		var tail = new Bone { Name = "tail", Length = 50, SetupPosition = new Vector2(50, 0) };
		root.AddChild(tail);
		skeleton.Roots.Add(root);
		skeleton.JiggleConstraints.Add(new JiggleConstraint { Name = "tail_jiggle", Bone = tail, Stiffness = 80, Damping = 6 });

		void Frame(float time, float baseTurn)
		{
			Pose(skeleton, () => root.Rotation = baseTurn);
			skeleton.StepPhysics(time);
		}
		Frame(0f, 0f);
		Assert.Equal(0f, tail.WorldRotation, 4);   // at rest to start with

		var angles = new List<float>();
		for (int f = 1; f <= 120; f++)
		{
			Frame(f / 30f, 1f);
			angles.Add(tail.WorldRotation);
		}
		Assert.True(angles[0] < 0.6f, $"lags behind at first ({angles[0]})");
		Assert.True(angles.Max() > 1.02f, $"swings past ({angles.Max()})");
		Assert.Equal(1f, angles[^1], 1);   // settled where the animation puts it

		Frame(0f, 1f);   // back to the start: from rest again
		Assert.Equal(1f, tail.WorldRotation, 4);

		skeleton.ResetPhysics();
		Frame(5f, 0f);
		Assert.Equal(0f, tail.WorldRotation, 4);
	}

	[Fact]
	public void Constraints_are_saved_undone_and_go_with_their_bones()
	{
		var (skeleton, a, b) = Pair();
		var follow = Follow(skeleton, b, a);
		follow.MoveMix = 0.4f;
		follow.ScaleMix = 0.2f;
		follow.SetupMix = 0.7f;
		var jiggle = new JiggleConstraint { Name = "b_jiggle", Bone = b, Stiffness = 120, Damping = 3, Gravity = -50, SetupMix = 0.8f };
		skeleton.JiggleConstraints.Add(jiggle);

		var dto = ProjectMapper.ToDto(skeleton, Array.Empty<Animation>(), 30);
		var (loaded, _) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(JsonSerializer.Serialize(dto, ProjectMapper.Json), ProjectMapper.Json)!);
		var c = loaded.CopyConstraints.Single();
		Assert.Equal(("b", "a", false, 1f, 0.4f, 0.2f, 0.7f), (c.Bone.Name, c.Source.Name, c.Local, c.RotateMix, c.MoveMix, c.ScaleMix, c.SetupMix));
		Near(follow.PositionOffset, c.PositionOffset);
		var j = loaded.JiggleConstraints.Single();
		Assert.Equal(("b", 120f, 3f, -50f, 0.8f), (j.Bone.Name, j.Stiffness, j.Damping, j.Gravity, j.SetupMix));

		var before = StructureSnapshot.Capture(skeleton, new List<Animation>());
		follow.MoveMix = 1f;
		skeleton.JiggleConstraints.Clear();
		before.Restore();
		Assert.Equal(0.4f, follow.MoveMix);
		Assert.Same(jiggle, skeleton.JiggleConstraints.Single());

		RigOps.DeleteBone(skeleton, Array.Empty<Animation>(), a);   // the bone followed: the follow goes
		Assert.Empty(skeleton.CopyConstraints);
		Assert.Single(skeleton.JiggleConstraints);
		RigOps.DeleteBone(skeleton, Array.Empty<Animation>(), b);
		Assert.Empty(skeleton.JiggleConstraints);
	}
}
