using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// The keyboard after the panels: a dropdown, button or checkbox used a moment ago doesn't keep
// Space and the other shortcuts to itself, and a text field is left with Enter, Esc or a click
// anywhere else (real keys and clicks, through the window, as a person's would come)
public class FocusTests
{
	private static void Press(MainWindow window, PhysicalKey key)
	{
		window.KeyPressQwerty(key, RawInputModifiers.None);
		window.KeyReleaseQwerty(key, RawInputModifiers.None);
		Dispatcher.UIThread.RunJobs();
	}

	private static void Click(MainWindow window, Control control)
	{
		var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
		window.MouseDown(at, MouseButton.Left);
		window.MouseUp(at, MouseButton.Left);
		Dispatcher.UIThread.RunJobs();
	}

	// (the timeline's bar, with the animation's dropdown and fields, shows while animating)
	private static void Animate(MainWindow window)
	{
		if (!window.State.AnimateMode) window.HandleKey(Key.K, KeyModifiers.None);
		Windows.Settle(window);
		Assert.True(window.State.AnimateMode);
	}

	[AvaloniaFact]
	public void Space_plays_after_a_dropdown_a_button_or_a_checkbox()
	{
		var window = Windows.Show();
		var state = window.State;
		Animate(window);
		int speed = window.SpeedBox.SelectedIndex;
		bool loop = window.AnimLoop.IsChecked == true;
		try
		{
			// With the keyboard on any of them: Space plays and stops, nothing opens or ticks
			foreach (var control in new Control[] { window.SpeedBox, window.AnimList, window.AnimLoop })
			{
				Assert.True(control.Focus());
				Press(window, PhysicalKey.Space);
				Assert.True(state.Playing);
				Press(window, PhysicalKey.Space);
				Assert.False(state.Playing);
			}
			Assert.False(window.SpeedBox.IsDropDownOpen);
			Assert.False(window.AnimList.IsDropDownOpen);
			Assert.Equal(loop, window.AnimLoop.IsChecked == true);

			// Picked from a dropdown's list: the keyboard goes back to the viewport
			Assert.True(window.SpeedBox.Focus());
			window.SpeedBox.IsDropDownOpen = true;
			Dispatcher.UIThread.RunJobs();
			window.SpeedBox.SelectedIndex = (speed + 1) % window.SpeedBox.ItemCount;
			window.SpeedBox.IsDropDownOpen = false;
			Dispatcher.UIThread.RunJobs();
			Assert.True(window.Viewport.IsFocused);
			Press(window, PhysicalKey.Space);
			Assert.True(state.Playing);
			Press(window, PhysicalKey.Space);
			Assert.False(state.Playing);
		}
		finally
		{
			window.SpeedBox.SelectedIndex = speed;
		}
	}

	[AvaloniaFact]
	public void A_field_is_left_with_Enter_Esc_or_a_click_elsewhere()
	{
		var window = Windows.Show();
		var state = window.State;
		Animate(window);
		string name = state.Animation.Name;

		// Typing: Space is no shortcut there; Esc puts back what was there and leaves
		Assert.True(window.AnimName.Focus());
		window.AnimName.Text = "walk";
		Press(window, PhysicalKey.Space);
		Assert.False(state.Playing);
		Press(window, PhysicalKey.Escape);
		Assert.Equal(name, window.AnimName.Text);
		Assert.Equal(name, state.Animation.Name);
		Assert.False(window.AnimName.IsKeyboardFocusWithin);
		Assert.True(window.Viewport.IsFocused);

		// Enter keeps what's typed, and leaves
		Assert.True(window.AnimName.Focus());
		window.AnimName.Text = "walk";
		Press(window, PhysicalKey.Enter);
		Assert.Equal("walk", state.Animation.Name);
		Assert.False(window.AnimName.IsKeyboardFocusWithin);

		// A click on the viewport keeps it too, and the keyboard's the viewport's: Space plays
		Assert.True(window.AnimName.Focus());
		window.AnimName.Text = "run";
		Click(window, window.Viewport);
		Assert.Equal("run", state.Animation.Name);
		Assert.True(window.Viewport.IsFocused);
		Press(window, PhysicalKey.Space);
		Assert.True(state.Playing);
		state.Playing = false;

		// So does a click on something that isn't a control at all (a panel's title)
		Assert.True(window.AnimName.Focus());
		window.AnimName.Text = "jog";
		Click(window, window.RigTitle);
		Assert.Equal("jog", state.Animation.Name);
		Assert.False(window.AnimName.IsKeyboardFocusWithin);

		// A number field the same: Esc puts its number back
		Assert.True(window.AnimFps.Focus());
		string fps = window.AnimFps.Text ?? "";
		window.AnimFps.Text = "7";
		Press(window, PhysicalKey.Escape);
		Assert.Equal(fps, window.AnimFps.Text);
		Assert.False(window.AnimFps.IsKeyboardFocusWithin);
	}
}
