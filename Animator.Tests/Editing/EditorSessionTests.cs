using System.IO;
using Animator.Viewer;
using Animator.Viewer.Rendering;

namespace Animator.Tests;

// The project as the editor works on it, without the window (EditorSession): opening objects in
// place on the scene timeline, going back to the scene, the scene playhead, whole projects
public class EditorSessionTests
{
	private static (EditorSession Session, SceneObject Hero, Animation Walk) TwoObjects()
	{
		var walk = new Animation { Name = "walk", Duration = 1f, Loop = true };
		var heroRig = new Skeleton();
		heroRig.Roots.Add(new Bone { Name = "root", Length = 10 });
		var hero = new SceneObject("hero", ObjectKind.Rigged, heroRig) { Animations = { walk } };
		var propRig = new Skeleton();
		propRig.Roots.Add(new Bone { Name = "root", Length = 10 });
		var prop = new SceneObject("prop", ObjectKind.Drawn, propRig) { Animations = { new Animation { Name = "spin", Duration = 2f } } };
		var scene = new Scene { Duration = 5f, Loop = false };
		scene.Objects.Add(hero);
		scene.Objects.Add(prop);
		var state = new EditorState(heroRig, walk);
		var session = new EditorSession(new AssetStore(), state);
		session.Replace(scene, null, null);
		return (session, hero, walk);
	}

	[Fact]
	public void A_scene_of_several_objects_opens_at_scene_level_with_nothing_to_undo()
	{
		var (session, hero, walk) = TwoObjects();
		Assert.True(session.SceneLevel);
		Assert.False(session.Editing);
		Assert.Same(hero, session.Active);
		Assert.Same(walk, session.State.Animation);
		Assert.Same(hero.Skeleton, session.State.Skeleton);
		Assert.False(session.State.Undo.IsDirty);
		Assert.Equal("Untitled", session.DocumentName);
	}

	[Fact]
	public void An_object_opens_where_its_clip_plays_and_the_scene_goes_on_from_its_timeline()
	{
		var (session, hero, walk) = TwoObjects();
		SceneOps.AddClip(hero, walk, 2f, 1f / 30);
		session.SceneState.Time = 2.5f;

		session.Enter(hero);
		Assert.True(session.Editing);
		Assert.Equal(0.5f, session.State.Time, 4);       // the clip's animation, half way in
		Assert.Equal(2f, session.SceneTimeAtZero, 4);    // its timeline's 0 is the clip's start
		Assert.Same(hero, session.SceneState.Selected);

		session.State.Time = 0.75f;
		Assert.Equal(2.75f, session.SceneTimeNow, 4);
		session.Leave();
		Assert.True(session.SceneLevel);
		Assert.Equal(2.75f, session.SceneState.Time, 4);
		Assert.Same(hero, session.CurrentObject);
	}

	[Fact]
	public void The_scene_playhead_stops_at_the_end_or_wraps_round()
	{
		var (session, _, _) = TwoObjects();
		session.State.Playing = true;
		session.SceneState.Time = 4.5f;
		session.AdvanceScene(1f);
		Assert.Equal(5f, session.SceneState.Time, 4);
		Assert.False(session.State.Playing);

		session.Scene.Loop = true;
		Assert.Equal(1f, session.WrapSceneTime(6f), 4);
		session.StepScene(-3);
		Assert.Equal(5f - 3f / session.State.Fps, session.SceneState.Time, 4);
	}

	[Fact]
	public void Opening_another_object_points_the_editor_at_it_and_drops_the_selection()
	{
		var (session, hero, _) = TwoObjects();
		session.Enter(hero);
		session.State.Selected = hero.Skeleton.Roots[0];
		var prop = session.Scene.Find("prop")!;
		Assert.True(session.Open(prop));
		Assert.Null(session.State.Selected);
		Assert.Same(prop.Skeleton, session.State.Skeleton);
		Assert.Equal("spin", session.State.Animation.Name);

		session.Scene.Objects.Remove(prop);   // (an undo took it away)
		session.KeepToWhatExists();
		Assert.Same(hero, session.Active);
	}

	[Fact]
	public void A_saved_project_opens_again_as_it_was()
	{
		var (session, _, _) = TwoObjects();
		string path = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid():N}.hqnap");
		try
		{
			session.State.Fps = 24;
			session.Save(path);
			Assert.Equal(path, session.ProjectPath);
			Assert.False(session.State.Undo.IsDirty);

			var loaded = Animator.Viewer.Files.ProjectFile.Load(path);
			var again = new EditorSession(new AssetStore(), new EditorState(new Skeleton(), new Animation()));
			again.Replace(loaded.Scene, loaded.Store, path);
			Assert.Equal(new[] { "hero", "prop" }, again.Scene.Objects.Select(o => o.Name));
			Assert.Equal(24, again.State.Fps);
			Assert.Equal(Path.GetFileNameWithoutExtension(path), again.DocumentName);
		}
		finally
		{
			File.Delete(path);
		}
	}

	// ======================= editing the scene =======================

	[Fact]
	public void Objects_are_added_copied_and_deleted_as_undo_steps()
	{
		var (session, hero, _) = TwoObjects();
		var made = session.AddRiggedObject(new System.Numerics.Vector2(10, 20));
		Assert.Equal(3, session.Scene.Objects.Count);
		Assert.Equal(new System.Numerics.Vector2(10, 20), made.Position);

		var copy = session.CopyObject(hero);
		Assert.Equal(4, session.Scene.Objects.Count);
		Assert.Equal(hero.Position + new System.Numerics.Vector2(30, -30), copy.Position);
		Assert.NotEqual(hero.Name, copy.Name);

		session.State.Undo.Undo();
		session.State.Undo.Undo();
		Assert.Equal(2, session.Scene.Objects.Count);

		// The open one deleted: the first one left opens
		session.Enter(hero);
		Assert.True(session.DeleteObject(hero));
		Assert.DoesNotContain(hero, session.Scene.Objects);
		Assert.Same(session.Scene.Objects[0], session.Active);
		Assert.Same(session.Active!.Skeleton, session.State.Skeleton);
		Assert.False(session.DeleteObject(session.Scene.Objects[0]));   // (not the last one)
	}

	[Fact]
	public void Object_names_placement_and_settings_change_only_when_they_do()
	{
		var (session, hero, _) = TwoObjects();
		var prop = session.Scene.Objects[1];
		Assert.False(session.RenameObject(hero, "prop"));      // another object's
		Assert.False(session.RenameObject(hero, "  "));
		Assert.True(session.RenameObject(hero, " star "));
		Assert.Equal("star", hero.Name);

		Assert.True(session.PlaceObject(hero, position: new System.Numerics.Vector2(5, 6), rotation: 0.5f));
		Assert.False(session.PlaceObject(hero, position: new System.Numerics.Vector2(5, 6)));   // there already
		Assert.True(session.SetObjectFlags(hero, visible: false, locked: true, export: true, onion: false));
		Assert.False(hero.Visible);

		// It can hang on the prop's bone, not on its own, and not on what it carries
		var choices = session.AttachChoices(hero);
		Assert.Null(choices[0].Holder);
		Assert.DoesNotContain(choices, c => c.Holder == hero);
		Assert.True(session.AttachObject(hero, choices.First(c => c.Holder == prop)));
		Assert.DoesNotContain(session.AttachChoices(prop), c => c.Holder == hero);

		Assert.False(session.SetPreview(hero, "no such animation"));
		Assert.False(session.SetPreview(hero, "walk"));   // it previews that already
	}

	[Fact]
	public void The_scene_and_its_clips_are_edited_as_undo_steps()
	{
		var (session, hero, walk) = TwoObjects();
		session.SceneState.Time = 4f;
		Assert.True(session.SetSceneLength(60));   // 2 s: the playhead comes inside
		Assert.Equal(2f, session.Scene.Duration, 4);
		Assert.Equal(2f, session.SceneState.Time, 4);
		Assert.True(session.SetSceneLoop(true));
		Assert.False(session.SetSceneLoop(true));
		Assert.True(session.SetStage(800, 600));

		var clip = session.AddClip(hero, walk, 0)!;
		Assert.Same(clip, session.SceneState.SelectedClip);
		Assert.Same(hero, session.SceneState.Selected);
		Assert.Equal(1f, clip.Length, 4);                       // one play long
		var next = session.AddClip(hero, walk, 0)!;             // taken there: it goes after
		Assert.Equal(1f, next.Start, 4);
		Assert.True(session.DeleteClip(hero, next));
		Assert.True(session.SetClipTiming(hero, clip, start: 15, length: 45));
		Assert.Equal(0.5f, clip.Start, 4);
		Assert.Equal(1.5f, clip.Length, 4);
		Assert.True(session.SetSceneLength(90));
		Assert.True(session.FitSceneToClips());
		Assert.Equal(2f, session.Scene.Duration, 4);
		Assert.True(session.SetClipLoop(clip, !clip.Loop));

		Assert.True(session.DeleteClip(hero, clip));
		Assert.Null(session.SceneState.SelectedClip);
		Assert.Empty(hero.Clips);
		session.State.Undo.Undo();
		Assert.Single(hero.Clips);
	}

	// ======================= the open object's animations =======================

	[Fact]
	public void Animations_are_added_copied_renamed_and_deleted_as_undo_steps()
	{
		var (session, hero, walk) = TwoObjects();
		session.Enter(hero);
		var made = session.NewAnimation();
		Assert.Equal("animation", made.Name);
		Assert.Same(made, session.State.Animation);
		Assert.Equal("animation2", session.NewAnimation().Name);

		session.SwitchAnimation(walk);
		var copy = session.DuplicateAnimation();
		Assert.Equal("walk_copy", copy.Name);
		Assert.Equal(hero.Animations.IndexOf(walk) + 1, hero.Animations.IndexOf(copy));

		// Renamed: the preview that named it follows; a name taken or empty is refused
		session.SwitchAnimation(walk);
		hero.PreviewAnimation = "walk";
		Assert.False(session.RenameAnimation("walk_copy"));
		Assert.False(session.RenameAnimation(" "));
		Assert.True(session.RenameAnimation("stroll"));
		Assert.Equal("stroll", hero.PreviewAnimation);
		session.State.Undo.Undo();
		Assert.Equal("walk", walk.Name);
		Assert.Equal("walk", hero.PreviewAnimation);

		Assert.True(session.SetAnimationLength(45));
		Assert.Equal(1.5f, walk.Duration, 4);
		Assert.False(session.SetAnimationLength(45));
		Assert.True(session.SetAnimationLoop(false));
		Assert.True(session.SetFps(24));
		Assert.Equal(24, session.State.Fps);
		session.State.Undo.Undo();
		Assert.Equal(30, session.State.Fps);

		while (hero.Animations.Count > 1) Assert.True(session.DeleteAnimation());
		Assert.False(session.DeleteAnimation());   // one always stays
	}

	[Fact]
	public void Picked_frames_become_an_animation_sequence_of_their_own()
	{
		var (session, hero, walk) = TwoObjects();
		session.Enter(hero);
		session.SwitchAnimation(walk);
		var piece = session.MakeSequence(10, 19, "step", split: true)!;
		Assert.Equal(hero.Animations.IndexOf(walk) + 1, hero.Animations.IndexOf(piece));
		Assert.Same(walk, session.State.Animation);   // still on the one it came from
		Assert.Equal(10, session.State.Frame);         // where they were
		Assert.True(walk.Duration < 1f);               // they left it
		Assert.NotNull(session.MakeSequence(0, 4, "start", split: false));
	}
}
