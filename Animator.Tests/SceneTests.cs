using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Milestone 9.5: a project is a scene of objects, each with its own rig and animations
public class SceneTests
{
	private static SceneObject Character(string name, string boneName, params string[] animations)
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = boneName, Length = 40 });
		var obj = new SceneObject(name, ObjectKind.Rigged, skeleton);
		foreach (var a in animations) obj.Animations.Add(new Animation { Name = a, Duration = 1f, Loop = true });
		return obj;
	}

	private static Scene RoundTrip(ProjectDto dto) =>
		ProjectMapper.SceneFromDto(JsonSerializer.Deserialize<ProjectDto>(
			JsonSerializer.Serialize(dto, ProjectMapper.Json), ProjectMapper.Json)!);

	[Fact]
	public void A_scene_with_several_objects_is_saved_and_loaded()
	{
		var scene = new Scene { Fps = 24 };
		var zombie = Character("zombie", "torso", "walk", "attack");
		zombie.Position = new Vector2(100, -20);
		zombie.Rotation = 0.5f;
		zombie.Scale = new Vector2(2, 2);
		zombie.PreviewAnimation = "walk";
		var background = Character("background", "root", "loop");
		background.Kind = ObjectKind.Drawn;
		background.Locked = true;
		background.Export = false;
		background.Onion = false;
		var hat = Character("hat", "root");
		hat.AttachObject = "zombie";
		hat.AttachBone = "torso";
		hat.Visible = false;
		scene.Objects.AddRange(new[] { background, zombie, hat });

		var loaded = RoundTrip(ProjectMapper.SceneToDto(scene));

		Assert.Equal(24, loaded.Fps);
		Assert.Equal(new[] { "background", "zombie", "hat" }, loaded.Objects.Select(o => o.Name));
		var z = loaded.Find("zombie")!;
		Assert.Equal(new Vector2(100, -20), z.Position);
		Assert.Equal(0.5f, z.Rotation);
		Assert.Equal(new Vector2(2, 2), z.Scale);
		Assert.Equal("walk", z.PreviewAnimation);
		Assert.Equal(new[] { "walk", "attack" }, z.Animations.Select(a => a.Name));
		Assert.NotNull(z.Skeleton.FindBone("torso"));

		var bg = loaded.Find("background")!;
		Assert.Equal(ObjectKind.Drawn, bg.Kind);
		Assert.True(bg.Locked);
		Assert.False(bg.Export);
		Assert.False(bg.Onion);

		var h = loaded.Find("hat")!;
		Assert.False(h.Visible);
		Assert.Same(z.Skeleton.FindBone("torso"), loaded.AttachmentBone(h));
	}

	[Fact]
	public void A_file_from_before_scenes_opens_as_one_object()
	{
		var old = Character("ignored", "torso", "idle", "run");
		var dto = ProjectMapper.ToDto(old.Skeleton, old.Animations, 30);   // the format 5 shape

		var loaded = RoundTrip(dto);

		var only = Assert.Single(loaded.Objects);
		Assert.Equal("Character", only.Name);
		Assert.Equal(ObjectKind.Rigged, only.Kind);
		Assert.NotNull(only.Skeleton.FindBone("torso"));
		Assert.Equal(new[] { "idle", "run" }, only.Animations.Select(a => a.Name));
	}

	[Fact]
	public void Object_names_must_be_unique()
	{
		var scene = new Scene();
		scene.Objects.Add(Character("tree", "root"));
		scene.Objects.Add(Character("tree2", "root"));
		Assert.Equal("tree3", scene.UniqueName("tree"));
		Assert.Equal("rock", scene.UniqueName("rock"));

		var dto = ProjectMapper.SceneToDto(scene);
		dto.Objects![1].Name = "tree";
		Assert.Throws<InvalidDataException>(() => ProjectMapper.SceneFromDto(dto));
	}

	[Fact]
	public void An_objects_transform_scales_then_turns_then_moves()
	{
		var obj = Character("o", "root");
		obj.Position = new Vector2(10, 0);
		obj.Rotation = MathF.PI / 2;
		obj.Scale = new Vector2(2, 2);

		var p = Vector2.Transform(new Vector2(1, 0), obj.Transform);   // (1,0) → (2,0) → (0,2) → (10,2)

		Assert.True(Vector2.Distance(new Vector2(10, 2), p) < 1e-4f, p.ToString());
	}
}
