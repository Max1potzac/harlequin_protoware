using SkiaSharp;

namespace Animator.Viewer;

// One open project, as an editor works on it, without the window: the scene and its pictures,
// the object open for editing (or the scene level, working on whole objects), the two playheads
// (the open object's timeline, and the scene's), where the project is saved, and posing it all
// at a moment. The Windows editor keeps one of these; so will the cross-platform one.
//
// One object is open at a time. Its rig is State.Skeleton and its animations State.Animations,
// and every panel and tool works on those: opening another object just points them elsewhere.
// Inside an object its timeline's 0 stands for a moment of the scene (SceneTimeAtZero), so the
// rest of the scene plays along in context.
public sealed class EditorSession
{
	public Scene Scene { get; set; }
	public AssetStore Assets { get; }
	public EditorState State { get; }
	public SceneState SceneState { get; } = new();

	public SceneObject? Active { get; private set; }   // open for editing (at scene level: the last one open)
	public bool SceneLevel { get; set; }               // true = working on whole objects
	public float SceneTimeAtZero { get; set; }         // inside an object: the scene time its timeline's 0 is

	public string? ProjectPath { get; set; }           // where Save puts it (null = never saved)
	public string DocumentName => ProjectPath == null ? "Untitled" : Path.GetFileNameWithoutExtension(ProjectPath);
	public string? ProjectFolder => ProjectPath != null ? Path.GetDirectoryName(ProjectPath) : null;

	public EditorSession(AssetStore assets, EditorState state, Scene? scene = null)
	{
		Assets = assets;
		State = state;
		Scene = scene ?? new Scene();
	}

	public bool Editing => !SceneLevel && Active != null;

	// The time the panels and tools work at: the timeline's inside an object, the scene playhead outside
	public float ClockTime => SceneLevel ? SceneState.Time : State.Time;

	// The scene time everything else is shown at (inside an object, following its timeline)
	public float SceneTimeNow => SceneLevel ? SceneState.Time : SceneTimeAtZero + State.Time;

	// One frame, in seconds
	public float FrameTime => 1f / Math.Max(1, State.Fps);

	// The object the buttons work on: the selected one at scene level, else the open one
	public SceneObject? CurrentObject => SceneLevel ? SceneState.Selected : Active;

	// ======================= opening objects =======================

	// Points the editor at this object's rig and animations (one gets an animation if it has
	// none). Switching objects drops the selection. True = another object than before.
	public bool Open(SceneObject obj)
	{
		if (obj.Animations.Count == 0)
			obj.Animations.Add(new Animation { Name = "animation", Duration = 2f, Loop = true });

		bool switching = Active != obj;
		if (switching)
		{
			State.Selected = null;
			State.SelectedSlot = null;
			State.SelectedKeys.Clear();
		}
		Active = obj;
		State.Skeleton = obj.Skeleton;
		State.Animations = obj.Animations;
		if (switching || !obj.Animations.Contains(State.Animation))
		{
			State.Animation = ScenePlayer.Preview(obj) ?? obj.Animations[0];
			State.SetFrame(State.Frame);   // keeps the playhead inside the animation
		}
		return switching;
	}

	// Opens an object in place: if a clip of it plays at this moment of the scene (or `via` was
	// picked), its animation opens at the matching frame, and the scene goes on along with it
	public void Enter(SceneObject obj, SceneClip? via = null)
	{
		float sceneTime = SceneTimeNow;
		if (via != null && !via.Covers(sceneTime)) sceneTime = via.Start;   // opened away from the playhead
		var clip = via ?? (obj.ClipAt(sceneTime) is { } playing && playing.Covers(sceneTime) ? playing : null);

		SceneLevel = false;
		Open(obj);
		if (clip != null && obj.Animations.Contains(clip.Animation))
		{
			State.Animation = clip.Animation;
			State.Time = MathF.Round(clip.AnimationTime(sceneTime) * State.Fps) / State.Fps;
			State.SelectedKeys.Clear();
		}
		SceneTimeAtZero = sceneTime - State.Time;
		SceneState.Selected = obj;
	}

	// Back to the scene, which goes on from where the object's timeline was
	public void Leave()
	{
		if (SceneLevel) return;
		SceneState.Time = WrapSceneTime(SceneTimeNow);
		SceneLevel = true;
		SceneState.Selected = Active;
		State.Selected = null;
		State.SelectedSlot = null;
	}

	// After an undo: the open object (or the selected one, or its selected clip) may be gone
	public void KeepToWhatExists()
	{
		if (Active == null || !Scene.Objects.Contains(Active)) Open(Scene.Objects[0]);
		if (SceneState.Selected != null && !Scene.Objects.Contains(SceneState.Selected)) SceneState.Selected = null;
		if (SceneState.SelectedClip is { } picked && SceneState.Selected?.Clips.Contains(picked) != true)
			SceneState.SelectedClip = null;
	}

	// ======================= whole projects =======================

	// Another project in place of this one: its scene and pictures (store: null keeps the
	// pictures, for a scene made here). It opens at its first object; a lone character ready to
	// edit, an animation or a scene of several objects at scene level (unless told).
	public void Replace(Scene scene, AssetStore? store, string? path, bool? sceneLevel = null)
	{
		Scene = scene;
		if (store != null) Assets.ReplaceWith(store);
		State.Fps = scene.Fps;
		Active = null;
		Open(scene.Objects[0]);
		SceneLevel = sceneLevel ?? (scene.Objects.Count > 1 || scene.Kind == ProjectKind.Animation);
		ProjectPath = path;
		Restart();
	}

	// A fresh start for a project just opened: nothing selected, playheads at 0, stopped, no
	// history, nothing unsaved
	public void Restart()
	{
		State.Selected = null;
		State.SelectedSlot = null;
		State.SelectedKeys.Clear();
		State.Time = 0;
		SceneState.Time = 0;
		SceneTimeAtZero = 0;
		SceneState.Selected = null;   // and the selected clip
		State.Playing = false;
		State.Undo.Clear();
		State.Undo.MarkSaved();
	}

	// Saves it where it was saved (or to `path`, which it then saves to)
	public void Save(string? path = null, SKBitmap? thumbnail = null)
	{
		path ??= ProjectPath ?? throw new InvalidOperationException("The project has never been saved: say where.");
		Scene.Fps = State.Fps;
		ProjectFile.Save(path, Scene, Assets, thumbnail);
		ProjectPath = path;
		State.Undo.MarkSaved();
	}

	// ======================= time and posing =======================

	// Every object at one time (ClockTime's kind): the open one with the animation being edited
	// (unless `posedElsewhere` poses it, as the mixer does), the others as the scene has them then
	public void PoseScene(float time, Func<bool>? posedElsewhere = null)
	{
		float sceneTime = Editing ? SceneTimeAtZero + time : time;
		foreach (var obj in Scene.Objects)
		{
			if (Editing && obj == Active)
			{
				if (posedElsewhere?.Invoke() == true) continue;
				State.PoseAt(time);
				if (State.AnimateMode) obj.Skeleton.StepPhysics(time);   // jiggles swing as time goes by
				else obj.Skeleton.ResetPhysics();
			}
			else ScenePlayer.Pose(obj, sceneTime);   // its clip there, or its preview animation looping
		}
	}

	// Scene time kept inside the scene: wrapped when it loops, held at the end when it doesn't
	public float WrapSceneTime(float t)
	{
		float d = Scene.Duration;
		if (d <= 0) return 0;
		if (t >= 0 && t <= d) return t;
		return Scene.Loop ? ((t % d) + d) % d : Math.Clamp(t, 0, d);
	}

	// Playing at scene level: the scene playhead runs, and loops or stops at the scene's end
	public void AdvanceScene(float dt)
	{
		float t = SceneState.Time + dt;
		if (t > Scene.Duration && !Scene.Loop)
		{
			t = Scene.Duration;
			State.Playing = false;
		}
		SceneState.Time = WrapSceneTime(t);
	}

	// The scene playhead some frames on (or back), inside the scene, stopped
	public void StepScene(int frames)
	{
		State.Playing = false;
		int last = (int)MathF.Round(Scene.Duration * State.Fps);
		int frame = (int)MathF.Round(SceneState.Time * State.Fps) + frames;
		SceneState.Time = Math.Clamp(frame, 0, last) / (float)State.Fps;
	}
}
