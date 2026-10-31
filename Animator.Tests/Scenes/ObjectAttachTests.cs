using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Milestone 9.5 step 3: drawn objects, and objects carried by another object's bone
public class ObjectAttachTests
{
	private static SceneObject Character(string name, string boneName = "torso")
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = boneName, Length = 40, SetupRotation = MathF.PI / 2 });
		var obj = new SceneObject(name, ObjectKind.Rigged, skeleton);
		obj.Animations.Add(new Animation { Name = "idle", Duration = 1f, Loop = true });
		return obj;
	}

	private static Scene SceneOf(params SceneObject[] objects)
	{
		var scene = new Scene();
		scene.Objects.AddRange(objects);
		foreach (var o in objects)
		{
			o.Skeleton.SetToSetupPose();
			o.Skeleton.UpdatePose();
		}
		return scene;
	}

	private static void Same(Matrix3x2 expected, Matrix3x2 actual)
	{
		float[] e = { expected.M11, expected.M12, expected.M21, expected.M22, expected.M31, expected.M32 };
		float[] a = { actual.M11, actual.M12, actual.M21, actual.M22, actual.M31, actual.M32 };
		for (int i = 0; i < 6; i++)
			Assert.True(MathF.Abs(e[i] - a[i]) < 1e-3f, $"expected {expected}, got {actual}");
	}

	[Fact]
	public void A_drawn_object_has_one_hidden_bone_and_keeps_its_kind_in_the_file()
	{
		var scene = new Scene();

		var cloud = SceneOps.AddDrawn(scene, "cloud", new Vector2(10, 20));

		Assert.Equal(ObjectKind.Drawn, cloud.Kind);
		Assert.Equal(new Vector2(10, 20), cloud.Position);
		var root = Assert.Single(cloud.Skeleton.Roots);
		Assert.Equal(0f, root.Length);
		Assert.Single(cloud.Animations);

		var json = JsonSerializer.Serialize(ProjectMapper.SceneToDto(scene), ProjectMapper.Json);
		var loaded = ProjectMapper.SceneFromDto(JsonSerializer.Deserialize<ProjectDto>(json, ProjectMapper.Json)!);
		Assert.Equal(ObjectKind.Drawn, loaded.Find("cloud")!.Kind);
	}

	[Fact]
	public void Attaching_and_freeing_keep_an_object_where_it_is()
	{
		var zombie = Character("zombie");
		zombie.Position = new Vector2(100, 0);
		zombie.Rotation = 0.3f;
		var hat = Character("hat", "root");
		hat.Position = new Vector2(120, 60);
		hat.Rotation = -0.5f;
		hat.Scale = new Vector2(2, 2);
		var scene = SceneOf(zombie, hat);
		var where = scene.WorldTransform(hat);

		Assert.True(SceneOps.Attach(scene, hat, zombie, "torso"));
		Assert.Equal("zombie", hat.AttachObject);
		Assert.Equal("torso", hat.AttachBone);
		Same(where, scene.WorldTransform(hat));

		// The bone turns: the hat goes along
		zombie.Skeleton.Roots[0].Rotation += 0.5f;
		zombie.Skeleton.UpdatePose();
		Assert.NotEqual(where.Translation, scene.WorldTransform(hat).Translation);

		var moved = scene.WorldTransform(hat);
		Assert.True(SceneOps.Attach(scene, hat, null, null));
		Assert.Null(hat.AttachObject);
		Same(moved, scene.WorldTransform(hat));
		Assert.False(SceneOps.Attach(scene, hat, null, null));   // already free
	}

	[Fact]
	public void An_object_cannot_carry_what_carries_it()
	{
		var zombie = Character("zombie");
		var hat = Character("hat", "root");
		var feather = Character("feather", "root");
		var scene = SceneOf(zombie, hat, feather);
		SceneOps.Attach(scene, hat, zombie, "torso");
		SceneOps.Attach(scene, feather, hat, "root");

		Assert.True(SceneOps.Carries(scene, zombie, feather));   // through the hat
		Assert.False(SceneOps.Attach(scene, zombie, feather, "root"));
		Assert.False(SceneOps.Attach(scene, zombie, zombie, "torso"));
		Assert.False(SceneOps.Attach(scene, hat, zombie, "no such bone"));
		Assert.Null(zombie.AttachObject);
	}

	[Fact]
	public void Deleting_a_holder_leaves_what_it_carried_where_it_was()
	{
		var zombie = Character("zombie");
		zombie.Position = new Vector2(-40, 15);
		var hat = Character("hat", "root");
		hat.Position = new Vector2(5, 30);
		var scene = SceneOf(zombie, hat);
		SceneOps.Attach(scene, hat, zombie, "torso");
		var where = scene.WorldTransform(hat);

		Assert.True(SceneOps.DeleteObject(scene, zombie));

		Assert.Null(hat.AttachObject);
		Same(where, scene.WorldTransform(hat));
	}

	[Fact]
	public void Renaming_or_deleting_the_carrying_bone_keeps_things_right()
	{
		var zombie = Character("zombie");
		var hat = Character("hat", "root");
		var scene = SceneOf(zombie, hat);
		SceneOps.Attach(scene, hat, zombie, "torso");

		SceneOps.RenameHolderBone(scene, zombie, "torso", "body");
		Assert.Equal("body", hat.AttachBone);

		zombie.Skeleton.Roots[0].Name = "body";
		var where = scene.WorldTransform(hat);
		SceneOps.ReleaseFromBones(scene, zombie, new HashSet<string> { "body" });
		Assert.Null(hat.AttachObject);
		Same(where, scene.WorldTransform(hat));
	}

	[Fact]
	public void A_place_with_mirroring_is_matched_exactly()
	{
		var obj = Character("o");
		var m = Matrix3x2.CreateScale(-1.5f, 0.75f) * Matrix3x2.CreateRotation(2.2f) * Matrix3x2.CreateTranslation(7, -3);

		SceneOps.SetTransform(obj, m);

		Same(m, obj.Transform);
	}

	private sealed class LogCommand(string name, List<string> log) : IEditCommand
	{
		public string Name => name;
		public void Undo() => log.Add($"undo {name}");
		public void Redo() => log.Add($"redo {name}");
	}

	[Fact]
	public void A_composite_step_undoes_backwards_and_redoes_forwards()
	{
		var log = new List<string>();
		var step = new CompositeCommand("both", new LogCommand("rig", log), new LogCommand("scene", log));

		step.Undo();
		step.Redo();

		Assert.Equal(new[] { "undo scene", "undo rig", "redo rig", "redo scene" }, log);
	}
}
