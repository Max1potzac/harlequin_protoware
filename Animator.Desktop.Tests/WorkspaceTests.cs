using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;

namespace Animator.Desktop.Tests;

// What the Windows editor has that the comparison found missing: Build and Animate (the timeline
// put away while building, the rig's tools while animating, the frame round the viewport), + Bone
// and Snap to ends, a bone deleted without what's on it, the skin banner, the play buttons, the
// objects to switch to, and the Animation menu's mixer, sound map and events
public class WorkspaceTests
{
	[AvaloniaFact]
	public void Build_puts_the_timeline_away_and_Animate_the_rigs_tools()
	{
		var window = Windows.Show();
		var state = window.State;
		Assert.False(state.AnimateMode);
		Assert.False(window.TimelineShown);
		Assert.NotNull(window.WorkspaceFrameBrush);
		Assert.True(window.AddBoneButton.IsEffectivelyVisible);
		Assert.True(window.SnapJointsBox.IsEffectivelyVisible);
		Windows.Shot(window, "workspace-build");

		// Nothing plays while building: play goes to Animate
		Assert.True(window.HandleKey(Key.Space, KeyModifiers.None));
		Assert.True(state.AnimateMode);
		Assert.True(state.Playing);
		state.Playing = false;
		window.Frame(0);
		Assert.True(window.TimelineShown);
		Assert.False(window.AddBoneButton.IsEffectivelyVisible);
		Assert.False(window.SnapJointsBox.IsEffectivelyVisible);
		Assert.Contains(window.MenuItemFor("anim.mixer").Header as string, new[] { "_Mixer…" });
		Windows.Shot(window, "workspace-animate");

		// Back to Build: playing stops
		state.Playing = true;
		Assert.True(window.HandleKey(Key.K, KeyModifiers.None));
		window.Frame(0);
		Assert.False(state.Playing);
		Assert.False(window.TimelineShown);
	}

	[AvaloniaFact]
	public void Bones_are_added_and_deleted_without_what_is_on_them()
	{
		var window = Windows.Show();
		var rig = window.State.Skeleton;
		var root = rig.Roots[0];

		// + Bone: a root bone with nothing picked, then a child of the picked one
		window.State.Selected = null;
		window.State.SelectedSlot = null;
		window.AddBoneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.Equal(2, rig.Roots.Count);
		window.State.Selected = root;
		window.AddBone();
		var arm = Assert.Single(root.Children);
		Assert.Same(arm, window.State.Selected);

		// Snap to ends off and on again
		window.SnapJointsBox.IsChecked = false;
		window.SnapJointsBox.IsChecked = true;

		// A part on the arm: deleting the arm hands it to the root
		using var picture = new SKBitmap(10, 10);
		picture.Erase(SKColors.Red);
		var hand = window.Session.CreatePart(arm, "hand", picture, 10, 10);
		var menu = window.DeleteBoneMenu(arm);
		Assert.Equal(2, menu.Items.Count);
		menu.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		Assert.Empty(root.Children);
		Assert.Same(root, hand.Bone);
		Assert.Contains(hand, rig.Slots);
	}

	[AvaloniaFact]
	public void The_skin_banner_and_switching_objects()
	{
		var window = Windows.Show();
		var session = window.Session;
		var first = session.Active!;

		// Editing a skin: the banner over the viewport
		var skin = session.MakeEmptySkin()!;
		window.EditSkin(skin);
		window.Frame(0);
		Assert.True(window.SkinBannerShown);
		Assert.Contains(skin.Name, window.SkinBannerText.Text);
		Windows.Shot(window, "workspace-skin");
		window.EditSkin(null);
		window.Frame(0);
		Assert.False(window.SkinBannerShown);

		// Another object: the list to switch with, inside either
		window.LeaveObject();
		window.AddRiggedObject();
		window.Frame(0);
		Assert.NotSame(first, session.Active);
		Assert.Equal(2, window.ObjectList.ItemCount);
		window.ObjectList.SelectedItem = window.ObjectList.Items.OfType<ListBoxItem>().First(i => i.Tag == first);
		Dispatcher.UIThread.RunJobs();
		Assert.Same(first, session.Active);
		Assert.False(session.SceneLevel);
	}

	[AvaloniaFact]
	public void The_viewports_tools_and_switches_as_in_the_Windows_editor()
	{
		var window = Windows.Show();
		var state = window.State;
		var settings = window.Settings;
		bool bones = settings.ShowBones;
		try
		{
			// The tools down the viewport's left: a click picks one, a key shows on its button
			Assert.True(window.ToolsShown);
			window.ToolButton(ViewTool.Rotate).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			window.Frame(0);
			Assert.True(window.ToolButton(ViewTool.Rotate).IsChecked);
			Assert.False(window.ToolButton(ViewTool.Select).IsChecked);
			Assert.True(window.HandleKey(Key.D1, KeyModifiers.None));
			window.Frame(0);
			Assert.True(window.ToolButton(ViewTool.Select).IsChecked);

			// The bones' toggle: hidden, and shown again
			var toggle = window.ViewToggle("view.bones");
			Assert.Equal(bones, toggle.IsChecked);
			toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			window.Frame(0);
			Assert.Equal(!bones, settings.ShowBones);
			Assert.Equal(!bones, toggle.IsChecked);
			toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.Equal(bones, settings.ShowBones);

			// The pictures' toggle
			window.ViewToggle("view.images").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			window.Frame(0);
			Assert.False(window.ViewToggle("view.images").IsChecked);
			window.ViewToggle("view.images").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			// Build | Animate over the viewport
			window.AnimateChoice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(state.AnimateMode);
			window.Frame(0);
			Assert.True(window.AnimateChoice.IsChecked);
			Assert.True(window.KeyButton.IsEffectivelyVisible);
			Windows.Shot(window, "workspace-tools");
			window.BuildChoice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.False(state.AnimateMode);

			// The scene: the object tools and the properties put away, the scene button opens the picked
			window.SceneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.True(window.Session.SceneLevel);
			window.Frame(0);
			Assert.False(window.ToolsShown);
			Assert.False(window.PropertiesShown);
			Assert.Same(window.Session.Scene.Objects[0], window.Session.SceneState.Selected);   // (the object left is picked)
			window.Session.SceneState.Selected = null;
			window.Frame(0);
			Assert.True(window.ObjectNone.IsEffectivelyVisible);
			Windows.Shot(window, "workspace-scene");
			window.Session.SceneState.Selected = window.Session.Scene.Objects[0];
			window.Frame(0);
			Assert.False(window.ObjectNone.IsEffectivelyVisible);
			window.SceneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.False(window.Session.SceneLevel);
			window.Frame(0);
			Assert.True(window.PropertiesShown);
		}
		finally
		{
			settings.ShowBones = bones;
			settings.Save();
		}
	}
}
