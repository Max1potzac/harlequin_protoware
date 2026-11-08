using Avalonia.Headless.XUnit;
using Xunit;

namespace Animator.Desktop.Tests;

// The bar over the timeline: the open object's animations
public class AnimationsTests
{
	[AvaloniaFact]
	public void Animations_are_added_renamed_lengthened_and_deleted()
	{
		var window = Windows.Show();
		var state = window.State;
		var first = state.Animation;
		Assert.Equal(first.Name, window.AnimName.Text);
		Assert.Equal("60", window.AnimLength.Text);

		// A new one, switched to, in the list
		window.Session.NewAnimation();
		window.RefreshAnimationsBar();
		Assert.Equal(2, state.Animations.Count);
		Assert.NotSame(first, state.Animation);
		Assert.Same(state.Animation, ((MainWindow.AnimationChoice)window.AnimList.SelectedItem!).Animation);

		// Renamed, lengthened; a length that isn't one changes nothing
		window.AnimName.Text = "walk";
		window.CommitAnimField(window.AnimName);
		Assert.Equal("walk", state.Animation.Name);
		window.AnimLength.Text = "30";
		window.CommitAnimField(window.AnimLength);
		Assert.Equal(30, state.FrameCount);
		window.AnimLength.Text = "lots";
		window.CommitAnimField(window.AnimLength);
		Assert.Equal(30, state.FrameCount);
		Assert.Equal("30", window.AnimLength.Text);
		window.AnimLoop.IsChecked = !state.Animation.Loop;
		Assert.Equal(window.AnimLoop.IsChecked, state.Animation.Loop);

		// Frames 10 to 19 copied into a sequence of their own
		window.MakeSequence((10, 19), "walk_step", split: false);
		Assert.Contains(state.Animations, a => a.Name == "walk_step");
		Windows.Shot(window, "animations-bar");

		// Picked from the list; deleted; the deletion undone
		window.AnimList.SelectedItem = ((System.Collections.IEnumerable)window.AnimList.ItemsSource!).Cast<MainWindow.AnimationChoice>().First(c => c.Animation == first);
		Assert.Same(first, state.Animation);
		Assert.True(window.Session.DeleteAnimation());
		window.RefreshAnimationsBar();
		Assert.DoesNotContain(first, state.Animations);
		state.Undo.Undo();
		Assert.Contains(first, state.Animations);
		Assert.Equal(state.Animations.Count, ((System.Collections.IEnumerable)window.AnimList.ItemsSource!).Cast<object>().Count());
	}

	[AvaloniaFact]
	public void The_bar_is_hidden_at_scene_level()
	{
		var window = Windows.Show();
		window.LeaveObject();
		Assert.False(window.AnimList.IsEffectivelyVisible);
		window.EnterObject(window.Session.Scene.Objects[0]);
		window.Frame(0);
		Assert.False(window.AnimList.IsEffectivelyVisible);   // (Build: the timeline's put away)
		window.State.AnimateMode = true;
		window.Frame(0);
		Assert.True(window.AnimList.IsEffectivelyVisible);
	}
}
