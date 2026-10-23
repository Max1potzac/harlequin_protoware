using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Milestone 9.6: clips of each object's animations on the scene timeline
public class SceneTimelineTests
{
	private const float Frame = 1f / 30;

	private static SceneObject Character(string name, params (string Name, float Duration)[] animations)
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "root", Length = 40 });
		var obj = new SceneObject(name, ObjectKind.Rigged, skeleton);
		foreach (var (n, d) in animations) obj.Animations.Add(new Animation { Name = n, Duration = d, Loop = true });
		return obj;
	}

	private static void Near(float expected, float actual) =>
		Assert.True(MathF.Abs(expected - actual) < 1e-4f, $"expected {expected}, got {actual}");

	[Fact]
	public void A_clip_plays_its_animation_from_its_start_and_holds_its_ends()
	{
		var walk = new Animation { Name = "walk", Duration = 1f };
		var clip = new SceneClip(walk, start: 2f, length: 1f);

		Near(0.5f, clip.AnimationTime(2.5f));
		Near(0f, clip.AnimationTime(1f));     // before: waits on its first frame
		Near(1f, clip.AnimationTime(4f));     // after: holds its last frame
	}

	[Fact]
	public void A_clip_longer_than_its_animation_loops_or_holds()
	{
		var walk = new Animation { Name = "walk", Duration = 1f };
		var looping = new SceneClip(walk, 0f, 3f) { Loop = true };
		var holding = new SceneClip(walk, 0f, 3f) { Loop = false };

		Near(0.5f, looping.AnimationTime(1.5f));
		Near(1f, holding.AnimationTime(1.5f));
	}

	[Fact]
	public void An_offset_trims_frames_off_the_front()
	{
		var walk = new Animation { Name = "walk", Duration = 1f };
		var clip = new SceneClip(walk, 2f, 0.5f) { Offset = 0.25f };

		Near(0.25f, clip.AnimationTime(2f));
		Near(0.5f, clip.AnimationTime(2.25f));
	}

	[Fact]
	public void The_clip_at_a_time_is_the_playing_one_or_the_one_held()
	{
		var hero = Character("hero", ("walk", 1f), ("jump", 1f));
		var walk = new SceneClip(hero.Animations[0], 1f, 1f);
		var jump = new SceneClip(hero.Animations[1], 3f, 1f);
		Assert.Null(hero.ClipAt(0f));   // no clips yet
		hero.Clips.AddRange(new[] { walk, jump });

		Assert.Same(walk, hero.ClipAt(0f));     // before the first: waits on it
		Assert.Same(walk, hero.ClipAt(1.5f));   // playing
		Assert.Same(walk, hero.ClipAt(2.5f));   // ended: held
		Assert.Same(jump, hero.ClipAt(3.5f));
		Assert.Same(jump, hero.ClipAt(9f));

		hero.Animations.Remove(hero.Animations[1]);   // a deleted animation's clip waits for an undo
		Assert.Same(walk, hero.ClipAt(3.5f));
	}

	[Fact]
	public void Added_clips_never_overlap()
	{
		var hero = Character("hero", ("walk", 1f), ("jump", 2f));
		var walk = hero.Animations[0];
		var jump = hero.Animations[1];

		var first = SceneOps.AddClip(hero, walk, 0f, Frame)!;
		var second = SceneOps.AddClip(hero, jump, 0.5f, Frame)!;   // inside the first: goes after it
		Near(1f, second.Start);
		Near(2f, second.Length);

		SceneOps.MoveClip(hero, second, 2f);   // a 1 s gap: 1..2
		var squeezed = SceneOps.AddClip(hero, jump, 1f, Frame)!;   // only 1 s of room
		Near(1f, squeezed.Start);
		Near(1f, squeezed.Length);

		// Inside a run of clips: it goes after the whole run
		var after = SceneOps.AddClip(hero, walk, 1.5f, Frame)!;
		Near(4f, after.Start);
		Assert.Equal(new[] { first, squeezed, second, after }, hero.Clips);   // kept in time order
	}

	[Fact]
	public void A_gap_shorter_than_a_frame_takes_no_clip()
	{
		var hero = Character("hero", ("walk", 1f));
		hero.Clips.Add(new SceneClip(hero.Animations[0], 0f, 1f));
		hero.Clips.Add(new SceneClip(hero.Animations[0], 1.01f, 1f));

		Assert.Null(SceneOps.AddClip(hero, hero.Animations[0], 1f, Frame));
		Assert.Equal(2, hero.Clips.Count);
	}

	[Fact]
	public void Dragged_clips_stop_at_their_neighbours()
	{
		var hero = Character("hero", ("walk", 1f));
		var walk = hero.Animations[0];
		var a = SceneOps.AddClip(hero, walk, 0f, Frame)!;
		var b = SceneOps.AddClip(hero, walk, 3f, Frame)!;

		SceneOps.MoveClip(hero, a, 2.5f);   // would overlap b: stops against it
		Near(2f, a.Start);
		SceneOps.MoveClip(hero, a, -5f);    // not before the scene's start
		Near(0f, a.Start);

		SceneOps.SetClipEnd(hero, a, 10f, Frame);   // longer: up to b
		Near(3f, a.Length);
		SceneOps.SetClipEnd(hero, a, -1f, Frame);   // shorter: one frame at least
		Near(Frame, a.Length);

		// Trimming the start keeps the end, and moves into the animation
		SceneOps.SetClipEnd(hero, a, 1f, Frame);
		SceneOps.SetClipStart(hero, a, 0.25f, Frame);
		Near(0.25f, a.Start);
		Near(0.75f, a.Length);
		Near(0.25f, a.Offset);
		SceneOps.SetClipStart(hero, a, -3f, Frame);   // back to the animation's first frame, no further
		Near(0f, a.Start);
		Near(0f, a.Offset);
	}

	[Fact]
	public void A_clip_can_be_fitted_to_one_play()
	{
		var hero = Character("hero", ("walk", 1.5f));
		var clip = SceneOps.AddClip(hero, hero.Animations[0], 0f, Frame)!;
		SceneOps.SetClipEnd(hero, clip, 4f, Frame);

		Assert.True(SceneOps.FitClip(hero, clip, Frame));
		Near(1.5f, clip.Length);
		Assert.False(SceneOps.FitClip(hero, clip, Frame));

		var scene = new Scene();
		scene.Objects.Add(hero);
		Near(1.5f, SceneOps.LastClipEnd(scene));
	}

	[Fact]
	public void The_scene_timeline_is_saved_and_loaded()
	{
		var hero = Character("hero", ("walk", 1f), ("jump", 2f));
		var walk = SceneOps.AddClip(hero, hero.Animations[0], 0.5f, Frame)!;
		walk.Loop = false;
		var jump = SceneOps.AddClip(hero, hero.Animations[1], 3f, Frame)!;
		jump.Offset = 0.5f;
		var scene = new Scene { Kind = ProjectKind.Animation, Duration = 7.5f, Loop = false, StageWidth = 640, StageHeight = 360 };
		scene.Objects.Add(hero);

		var dto = ProjectMapper.SceneToDto(scene);
		dto.Objects![0].Clips!.Add(new ClipDto { Animation = "no such animation", Start = 9, Length = 1 });   // dropped
		var loaded = ProjectMapper.SceneFromDto(JsonSerializer.Deserialize<ProjectDto>(JsonSerializer.Serialize(dto, ProjectMapper.Json), ProjectMapper.Json)!);

		Assert.Equal(ProjectKind.Animation, loaded.Kind);
		Near(7.5f, loaded.Duration);
		Assert.False(loaded.Loop);
		Assert.Equal(640, loaded.StageWidth);
		Assert.Equal(360, loaded.StageHeight);

		var h = loaded.Find("hero")!;
		Assert.Equal(2, h.Clips.Count);
		Assert.Same(h.FindAnimation("walk"), h.Clips[0].Animation);
		Near(0.5f, h.Clips[0].Start);
		Assert.False(h.Clips[0].Loop);
		Assert.Same(h.FindAnimation("jump"), h.Clips[1].Animation);
		Near(0.5f, h.Clips[1].Offset);
	}

	[Fact]
	public void A_file_from_before_the_scene_timeline_gets_the_defaults()
	{
		var hero = Character("hero", ("walk", 1f));
		var dto = ProjectMapper.ToDto(hero.Skeleton, hero.Animations, 24);   // no scene fields at all

		var loaded = ProjectMapper.SceneFromDto(dto);

		Assert.Equal(ProjectKind.Character, loaded.Kind);
		Near(5f, loaded.Duration);
		Assert.True(loaded.Loop);
		Assert.False(loaded.HasStage);
		Assert.Empty(loaded.Objects[0].Clips);
	}

	[Fact]
	public void Undo_brings_back_the_clips_and_the_scene_settings()
	{
		var hero = Character("hero", ("walk", 1f));
		var scene = new Scene { Duration = 4f };
		scene.Objects.Add(hero);
		var clip = SceneOps.AddClip(hero, hero.Animations[0], 0f, Frame)!;
		var before = SceneSnapshot.Capture(scene);

		SceneOps.MoveClip(hero, clip, 2f);
		hero.Clips.Add(new SceneClip(hero.Animations[0], 3.5f, 0.5f));
		scene.Duration = 9f;
		scene.StageWidth = 100;
		before.Restore();

		Assert.Same(clip, Assert.Single(hero.Clips));
		Near(0f, clip.Start);
		Near(4f, scene.Duration);
		Assert.Equal(0, scene.StageWidth);
	}

	[Fact]
	public void A_copied_object_has_its_own_clips_on_its_own_animations()
	{
		var hero = Character("hero", ("walk", 1f));
		SceneOps.AddClip(hero, hero.Animations[0], 1f, Frame);
		var scene = new Scene();
		scene.Objects.Add(hero);

		var (copy, _) = SceneOps.Duplicate(scene, hero);

		var copied = Assert.Single(copy.Clips);
		Assert.Contains(copied.Animation, copy.Animations);
		Assert.DoesNotContain(copied.Animation, hero.Animations);
		Near(1f, copied.Start);
	}
}
