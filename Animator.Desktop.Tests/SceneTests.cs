using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;

namespace Animator.Desktop.Tests;

// The scene: File > New (a character, the demo one or not; an animation with its stage), the
// objects at scene level (added, copied, deleted, their flags), the scene's bar, clips (added,
// timed, the timeline's menu, Delete), and bringing in from another object
public class SceneTests
{
	private static void Finish(Task task)
	{
		var until = DateTime.UtcNow.AddSeconds(30);
		while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		Assert.True(task.IsCompleted);
		task.GetAwaiter().GetResult();
	}

	private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

	private static void ClickItem(ItemsControl menu, string header) =>
		menu.Items.OfType<MenuItem>().First(i => i.Header as string == header).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

	[AvaloniaFact]
	public void A_new_character_starts_from_the_demo_one()
	{
		var window = Windows.Show();
		window.AnswerNewProject = dialog =>
		{
			Assert.True(dialog.CharacterChoice.IsChecked);
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Finish(window.NewProject());
		Assert.False(window.Session.SceneLevel);
		var rig = window.State.Skeleton;
		Assert.NotNull(rig.FindBone("torso"));
		Assert.Equal(5, rig.Slots.Count);
		Assert.Equal("idle", window.State.Animation.Name);
		Windows.Shot(window, "scene-demo");
	}

	[AvaloniaFact]
	public void An_animation_project_is_a_scene_of_objects_and_clips()
	{
		var window = Windows.Show();
		var session = window.Session;

		// File > New: an animation, its stage and length
		window.AnswerNewProject = dialog =>
		{
			dialog.AnimationChoice.IsChecked = true;
			dialog.StageWidthBox.Text = "8";
			dialog.TryOk();
			Assert.Contains("16 to 4096", dialog.ErrorText.Text);
			dialog.StageWidthBox.Text = "640";
			dialog.StageHeightBox.Text = "360";
			dialog.LengthBox.Text = "48";
			dialog.FpsBox.Text = "24";
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Finish(window.NewProject());
		var scene = session.Scene;
		Assert.True(session.SceneLevel);
		Assert.Equal(640, scene.StageWidth);
		Assert.Equal(24, window.State.Fps);
		var background = Assert.Single(scene.Objects);
		Assert.True(background.Locked);
		window.Frame(0);
		Assert.Equal("48", window.SceneLength.Text);

		// The scene's bar
		window.SceneLength.Text = "72";
		window.CommitSceneField(window.SceneLength);
		Assert.Equal(3f, scene.Duration, 3);
		window.StageHeight.Text = "x";
		window.CommitSceneField(window.StageHeight);
		Assert.Equal(360, scene.StageHeight);

		// A rigged object: it opens; back at the scene, it's picked and its settings show
		window.AddRiggedObject();
		Assert.False(session.SceneLevel);
		var actor = session.Active!;
		Assert.Equal(2, scene.Objects.Count);
		window.LeaveObject();
		session.SceneState.Selected = actor;
		window.Frame(0);
		Assert.Equal(2, window.ObjectList.ItemCount);
		Assert.True(window.CopyObjectButton.IsEnabled);
		window.ObjectLocked.IsChecked = true;
		Assert.True(actor.Locked);
		window.ObjectVisible.IsChecked = false;
		Assert.False(actor.Visible);
		window.State.Undo.Undo();
		Assert.True(actor.Visible);

		// Copied, deleted, the delete undone
		window.CopyPickedObject();
		Assert.Equal(3, scene.Objects.Count);
		var copy = session.SceneState.Selected!;
		Assert.NotSame(actor, copy);
		Click(window.DeleteObjectButton);
		Assert.Equal(2, scene.Objects.Count);
		window.State.Undo.Undo();
		Assert.Equal(3, scene.Objects.Count);

		// A clip: added at a frame, timed in the panel
		session.SceneState.Selected = actor;
		var clip = window.AddClip(actor, actor.Animations[0], 10)!;
		Assert.NotNull(clip);
		Assert.Same(clip, session.SceneState.SelectedClip);
		window.Frame(0);
		Assert.True(window.ClipNumbers["Start"].IsEffectivelyVisible);
		window.ClipNumbers["Start"].Type("12");
		Assert.Equal(12, session.ToFrames(clip.Start));
		window.ClipLoop.IsChecked = !clip.Loop;
		bool loop = clip.Loop;
		window.Frame(0);
		Assert.Equal(loop, window.ClipLoop.IsChecked);
		Windows.Shot(window, "scene-clip");

		// The timeline's menu: the scene ends at a frame
		var menu = window.ClipMenu(actor, clip, 60);
		Assert.Contains(menu.Items.OfType<MenuItem>(), i => i.Header as string == "Delete clip");
		ClickItem(menu, "End the scene at frame 60");
		Assert.Equal(60, session.ToFrames(scene.Duration));

		// Delete: the picked clip first, then the picked object
		Assert.True(window.HandleKey(Key.Delete, KeyModifiers.None));
		Assert.Empty(actor.Clips);
		Assert.True(window.HandleKey(Key.Delete, KeyModifiers.None));
		Assert.DoesNotContain(actor, scene.Objects);

		// A blank drawn object: it opens, in the paint editor
		window.AnswerNewDrawnObject = dialog =>
		{
			dialog.NameBox.Text = "sky";
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Finish(window.AddBlankDrawnObject());
		Assert.False(session.SceneLevel);
		Assert.Equal(ObjectKind.Drawn, session.Active!.Kind);
		Assert.True(session.PaintOpen);
		window.LeaveObject();
		Assert.False(session.PaintOpen);
	}

	[AvaloniaFact]
	public void Animations_are_brought_in_from_another_object()
	{
		var window = Windows.Show();
		var session = window.Session;
		var root = window.State.Skeleton.Roots[0];
		using var picture = new SKBitmap(20, 20);
		picture.Erase(SKColors.Teal);
		window.Session.CreatePart(root, "hat", picture, 20, 20);
		var original = session.Active!;

		// Nothing to bring in from yet
		window.AnswerBringIn = _ => Task.FromResult(false);
		Finish(window.BringIn());
		Assert.Contains("no other object", window.BroughtInReport);

		// A copy of it, with its own animation; then back into the first
		window.LeaveObject();
		session.SceneState.Selected = original;
		window.CopyPickedObject();
		var copy = session.SceneState.Selected!;
		copy.Animations[0].Name = "walk";
		window.EnterObject(original);
		window.AnswerBringIn = dialog =>
		{
			Assert.Same(copy, dialog.Source);
			Assert.Single(dialog.AnimationRows);
			Assert.Contains(dialog.PartRows, r => r.Source == "hat");
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Finish(window.BringIn());
		Assert.Equal(2, window.State.Animations.Count);
		Assert.Contains(window.State.Animations, a => a.Name.StartsWith("walk"));
		Assert.Contains("animation(s)", window.BroughtInReport);

		// From Animate projects: none added, nothing picked
		window.AnswerBatchBringIn = dialog =>
		{
			dialog.TryOk();
			Assert.Contains("Add the projects", dialog.Problem.Text);
			return Task.FromResult(false);
		};
		Finish(window.BatchBringIn());
	}
}
