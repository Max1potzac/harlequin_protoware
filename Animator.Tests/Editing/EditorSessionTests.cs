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
}
