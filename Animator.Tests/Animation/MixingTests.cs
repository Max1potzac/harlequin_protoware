using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Step 24: animations mixed: on tracks over each other, fading into each other (the mixer, and
// scene clips fading in), and the fade times kept for the game
public class MixingTests
{
	// A body with a leg and an arm (both straight out at rest), and a mouth with two drawings
	private static (Skeleton Skeleton, Bone Leg, Bone Arm, Slot Mouth) Body()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root" };
		var leg = new Bone { Name = "leg", Length = 30 };
		var arm = new Bone { Name = "arm", Length = 30 };
		root.AddChild(leg);
		root.AddChild(arm);
		skeleton.Roots.Add(root);
		var mouth = new Slot("mouth", root) { SetupAttachment = "shut" };
		skeleton.Slots.Add(mouth);
		skeleton.DefaultSkin.Set("mouth", new Attachment { Name = "shut" });
		skeleton.DefaultSkin.Set("mouth", new Attachment { Name = "open" });
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		return (skeleton, leg, arm, mouth);
	}

	// An animation holding one bone at one angle (and the mouth open, if asked)
	private static Animation Holding(string name, string bone, float angle, bool open = false, float duration = 1f)
	{
		var a = new Animation { Name = name, Duration = duration, Loop = true };
		var turn = new RotateTimeline(bone);
		turn.Track.Add(0f, angle);
		a.Timelines.Add(turn);
		if (open)
		{
			var mouth = new AttachmentTimeline("mouth");
			mouth.Add(0f, "open");
			a.Timelines.Add(mouth);
		}
		return a;
	}

	[Fact]
	public void A_layer_blends_what_it_keys_by_its_weight_and_leaves_the_rest()
	{
		var (skeleton, leg, arm, mouth) = Body();
		skeleton.SetToSetupPose();
		Mixing.Layer(skeleton, Holding("kick", "leg", 1f, open: true), 0f, 0.25f);
		Assert.Equal(0.25f, leg.Rotation, 4);
		Assert.Equal(0f, arm.Rotation);
		Assert.Equal("shut", mouth.ActiveAttachment);   // a drawing switches halfway, not before

		skeleton.SetToSetupPose();
		Mixing.Layer(skeleton, Holding("kick", "leg", 1f, open: true), 0f, 0.75f);
		Assert.Equal(0.75f, leg.Rotation, 4);
		Assert.Equal("open", mouth.ActiveAttachment);
	}

	[Fact]
	public void Tracks_lay_animations_over_each_other_each_changing_only_its_own_bones()
	{
		var (skeleton, leg, arm, _) = Body();
		var walk = Holding("walk", "leg", 0.5f);
		walk.GetOrCreate("arm", n => new RotateTimeline(n)).Track.Add(0f, -0.3f);   // walking swings the arm too
		var shoot = Holding("shoot", "arm", 1.2f);
		var preview = new MixPreview();
		preview.Tracks[0].Animation = walk;
		preview.Tracks[1].Animation = shoot;

		preview.Pose(skeleton, 0.2f);
		Assert.Equal(0.5f, leg.Rotation, 4);   // the legs walk
		Assert.Equal(1.2f, arm.Rotation, 4);   // the arm shoots, over the walk's swing

		preview.Tracks[1].Weight = 0.5f;
		preview.Pose(skeleton, 0.2f);
		Assert.Equal(0.45f, arm.Rotation, 4);  // halfway between -0.3 and 1.2
	}

	[Fact]
	public void A_fade_goes_the_short_way_round_and_the_mixer_tries_it_out()
	{
		var (skeleton, leg, _, _) = Body();
		var a = Holding("a", "leg", 0.1f);
		var b = Holding("b", "leg", 6.2f);   // nearly a full turn: the same as about -0.083
		skeleton.SetToSetupPose();
		Mixing.Crossfade(skeleton, a, 0f, b, 0f, 0.5f);
		Assert.Equal(0.0084f, leg.Rotation, 3);   // not halfway round the other way (3.15)

		var walk = Holding("walk", "leg", 0f, duration: 1f);
		var run = Holding("run", "leg", 1f, duration: 1f);
		var preview = new MixPreview { Transition = (walk, run, 0.4f) };
		preview.Pose(skeleton, 0.5f);
		Assert.Equal(0f, leg.Rotation, 4);     // the first playing once through
		preview.Pose(skeleton, 1.2f);
		Assert.Equal(0.5f, leg.Rotation, 3);   // halfway through the fade
		preview.Pose(skeleton, 1.6f);
		Assert.Equal(1f, leg.Rotation, 4);     // faded
		Assert.Equal(2.25f, preview.TransitionLength, 4);
	}

	[Fact]
	public void A_scene_clip_fades_in_from_the_clip_before()
	{
		var (skeleton, leg, _, _) = Body();
		var obj = new SceneObject("hero", ObjectKind.Rigged, skeleton);
		var walk = Holding("walk", "leg", 0f);
		var run = Holding("run", "leg", 1f);
		obj.Animations.AddRange(new[] { walk, run });
		obj.Clips.Add(new SceneClip(walk, 0f, 1f));
		var into = new SceneClip(run, 1f, 1f) { MixIn = 0.5f };
		obj.Clips.Add(into);

		ScenePlayer.Pose(obj, 1.25f);
		Assert.Equal(0.5f, leg.Rotation, 3);   // halfway into the fade
		ScenePlayer.Pose(obj, 1.6f);
		Assert.Equal(1f, leg.Rotation, 4);
		into.MixIn = 0f;
		ScenePlayer.Pose(obj, 1.25f);
		Assert.Equal(1f, leg.Rotation, 4);     // a cut
		Assert.Same(obj.Clips[0], obj.ClipBefore(into));
	}

	[Fact]
	public void Fade_times_are_kept_per_pair_renamed_saved_and_undone()
	{
		var (skeleton, _, _, _) = Body();
		var plan = skeleton.Mixes;
		Assert.Equal(0.2f, plan.Duration("walk", "run"));
		plan.Set("walk", "run", 0.35f);
		plan.Set("walk", "run", 0.3f);   // (set again: replaced)
		plan.Default = 0.1f;
		Assert.Equal(0.3f, plan.Duration("walk", "run"));
		Assert.Equal(0.1f, plan.Duration("run", "walk"));
		plan.Rename("run", "sprint");
		Assert.Equal(0.3f, plan.Duration("walk", "sprint"));

		var before = StructureSnapshot.Capture(skeleton, new List<Animation>());
		plan.Remove("walk", "sprint");
		before.Restore();
		Assert.Equal(0.3f, plan.Duration("walk", "sprint"));

		var obj = new SceneObject("hero", ObjectKind.Rigged, skeleton);
		var walk = Holding("walk", "leg", 0f);
		var sprint = Holding("sprint", "leg", 1f);
		obj.Animations.AddRange(new[] { walk, sprint });
		obj.Clips.Add(new SceneClip(walk, 0f, 1f));
		obj.Clips.Add(new SceneClip(sprint, 1f, 1f) { MixIn = 0.25f });
		var scene = new Scene();
		scene.Objects.Add(obj);
		var loaded = ProjectMapper.SceneFromDto(JsonSerializer.Deserialize<ProjectDto>(
			JsonSerializer.Serialize(ProjectMapper.SceneToDto(scene), ProjectMapper.Json), ProjectMapper.Json)!).Objects.Single();
		Assert.Equal(0.3f, loaded.Skeleton.Mixes.Duration("walk", "sprint"));
		Assert.Equal(0.1f, loaded.Skeleton.Mixes.Default);
		Assert.Equal(new[] { 0f, 0.25f }, loaded.Clips.Select(c => c.MixIn));
	}
}
