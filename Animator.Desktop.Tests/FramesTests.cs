using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Animator.Desktop.Tests;

// Frame by frame: a part's drawings (new, copied, longer, shorter, renamed, deleted, hidden) and
// the onion skin's settings
public class FramesTests
{
	[AvaloniaFact]
	public void A_part_is_drawn_frame_by_frame()
	{
		var window = Windows.Show();
		var state = window.State;
		var root = state.Skeleton.Roots[0];
		var face = window.Session.CreatePart(root, "face", null, 20, 20);
		state.Selected = root;
		state.SelectedSlot = face;
		window.Frame(0);
		Assert.Single(DrawingOps.DrawingNames(state.Skeleton, face));
		string first = window.Session.CurrentDrawing(face)!;

		// Animate, frame 5: F6, a copy shown from here on
		state.AnimateMode = true;
		state.SetFrame(5);
		Assert.True(window.HandleKey(Key.F6, KeyModifiers.None));
		window.Frame(0);
		var names = DrawingOps.DrawingNames(state.Skeleton, face);
		Assert.Equal(2, names.Count);
		string copy = window.Session.CurrentDrawing(face)!;
		Assert.NotEqual(first, copy);
		Assert.Equal(copy, window.DrawingName.Text);
		state.SetFrame(4);
		window.Frame(0);
		Assert.Equal(first, window.Session.CurrentDrawing(face));
		Assert.Equal(first, window.DrawingName.Text);

		// F5 on the first: a frame longer, so the copy starts a frame later
		Assert.True(window.HandleKey(Key.F5, KeyModifiers.None));
		state.SetFrame(5);
		Assert.Equal(first, window.Session.CurrentDrawing(face));
		state.SetFrame(6);
		Assert.Equal(copy, window.Session.CurrentDrawing(face));
		Assert.True(window.HandleKey(Key.F5, KeyModifiers.None));   // (the last drawing: nothing after it)
		Assert.Contains("Nothing comes after", window.PartHint.Text);

		// Renamed in its box
		window.Frame(0);
		window.DrawingName.Text = "blink";
		window.CommitDrawingName();
		Assert.Contains("blink", DrawingOps.DrawingNames(state.Skeleton, face));
		Assert.Equal("blink", window.Session.CurrentDrawing(face));

		// Hidden here, from the list
		window.Frame(0);
		window.DrawingBox.SelectedItem = window.DrawingBox.Items.Cast<object>().Last();   // (hidden)
		Assert.Null(window.Session.CurrentDrawing(face));
		Windows.Shot(window, "frames-drawings");
		state.Undo.Undo();
		Assert.Equal("blink", window.Session.CurrentDrawing(face));

		// F7 in Build: a new blank rest drawing
		state.AnimateMode = false;
		Assert.True(window.HandleKey(Key.F7, KeyModifiers.None));
		Assert.Equal(3, DrawingOps.DrawingNames(state.Skeleton, face).Count);
	}

	[AvaloniaFact]
	public void The_onion_skin_is_switched_and_set()
	{
		var window = Windows.Show();
		Assert.False(window.Onion.Enabled);
		Assert.True(window.HandleKey(Key.O, KeyModifiers.None));
		window.Frame(0);
		Assert.True(window.OnionBox.IsChecked);

		window.OnionBefore.SelectedItem = 4;
		window.OnionStep.SelectedItem = 2;
		Assert.Equal(4, window.Onion.Before);
		Assert.Equal(2, window.Onion.Step);
		window.OnionBox.IsChecked = false;
		Assert.False(window.Onion.Enabled);
		Assert.Equal(4, window.Onion.Before);
	}
}
