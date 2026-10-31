using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Milestone 9.5 step 3: a bone can be snapped to its parent's end ("On parent's end")
public class BoneSnapTests
{
	private static (Skeleton Skeleton, Bone Parent, Bone Child) Arm()
	{
		var skeleton = new Skeleton();
		var parent = new Bone { Name = "upper", Length = 100, SetupRotation = 0.4f };
		var child = new Bone { Name = "fore", Length = 80, SetupPosition = new Vector2(30, 5) };
		parent.AddChild(child);
		skeleton.Roots.Add(parent);
		return (skeleton, parent, child);
	}

	private static void Pose(Skeleton skeleton)
	{
		skeleton.SetToSetupPose();
		skeleton.UpdatePose();
	}

	private static void Near(Vector2 expected, Vector2 actual) =>
		Assert.True(Vector2.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");

	[Fact]
	public void A_connected_bone_sits_on_its_parents_end_and_follows_its_length()
	{
		var (skeleton, parent, child) = Arm();

		Assert.True(RigOps.SetConnected(child, true));
		Assert.Equal(new Vector2(100, 0), child.SetupPosition);

		parent.Length = 150;   // the parent gets longer: the joint goes along
		Pose(skeleton);
		Assert.Equal(new Vector2(150, 0), child.SetupPosition);
		Near(parent.TipWorld, child.World.Translation);
	}

	[Fact]
	public void Freeing_a_connected_bone_keeps_it_where_it_is()
	{
		var (_, parent, child) = Arm();
		RigOps.SetConnected(child, true);

		Assert.True(RigOps.SetConnected(child, false));
		Assert.False(child.Connected);
		Assert.Equal(new Vector2(100, 0), child.SetupPosition);   // not back at (30, 5)

		parent.Length = 60;   // free now: it stays put
		Assert.Equal(new Vector2(100, 0), child.SetupPosition);
	}

	[Fact]
	public void A_root_bone_has_no_parents_end_to_snap_to()
	{
		var (_, parent, _) = Arm();
		Assert.False(RigOps.SetConnected(parent, true));
		Assert.False(parent.Connected);
	}

	[Fact]
	public void A_connected_bone_moves_onto_its_new_parents_end()
	{
		var (skeleton, _, fore) = Arm();
		var other = new Bone { Name = "other", Length = 70, SetupPosition = new Vector2(-50, 20), SetupRotation = 1.2f };
		skeleton.Roots.Add(other);
		RigOps.SetConnected(fore, true);

		Assert.True(RigOps.Reparent(skeleton, fore, other));
		Pose(skeleton);
		Assert.Equal(new Vector2(70, 0), fore.SetupPosition);
		Near(other.TipWorld, fore.World.Translation);

		// Made a root bone, it lets go and stays where it was
		var before = fore.World.Translation;
		Assert.True(RigOps.Reparent(skeleton, fore, null));
		Assert.False(fore.Connected);
		Pose(skeleton);
		Near(before, fore.World.Translation);
	}

	[Fact]
	public void Connected_is_saved_and_loaded()
	{
		var (skeleton, _, child) = Arm();
		RigOps.SetConnected(child, true);

		var json = JsonSerializer.Serialize(ProjectMapper.ToDto(skeleton, new List<Animation>(), 30), ProjectMapper.Json);
		var (loaded, _) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(json, ProjectMapper.Json)!);

		var fore = loaded.FindBone("fore")!;
		Assert.True(fore.Connected);
		Assert.Equal(new Vector2(100, 0), fore.SetupPosition);
		Assert.False(loaded.FindBone("upper")!.Connected);
	}

	[Fact]
	public void Undo_brings_back_whether_a_bone_was_connected()
	{
		var (skeleton, _, child) = Arm();
		var animations = new List<Animation>();
		var free = StructureSnapshot.Capture(skeleton, animations);
		var pose = PoseSnapshot.Capture(child, new Animation());

		RigOps.SetConnected(child, true);
		var joined = StructureSnapshot.Capture(skeleton, animations);

		free.Restore();
		Assert.False(child.Connected);
		Assert.Equal(new Vector2(30, 5), child.SetupPosition);

		joined.Restore();
		Assert.True(child.Connected);
		Assert.Equal(new Vector2(100, 0), child.SetupPosition);

		pose.Restore();   // a joint drag's undo step
		Assert.False(child.Connected);
		Assert.Equal(new Vector2(30, 5), child.SetupPosition);
	}
}
