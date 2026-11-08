using System.Numerics;
using Animator.Core.Keyframing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;

namespace Animator.Desktop.Tests;

// The timeline's Curves view (the graph editor): switched with Ctrl+G or its box, keys dragged
// there with the mouse, and the key menu's easing for them
public class GraphTests
{
	// The root bone turned at frames 0 and 10, picked, in Animate
	private static (MainWindow Window, RotateTimeline Turn) Animated()
	{
		var window = Windows.Show();
		var state = window.State;
		var root = state.Skeleton.Roots[0];
		state.AnimateMode = true;
		state.Pick(new object[] { root });
		state.SetFrame(0);
		window.KeyThePose();
		state.SetFrame(10);
		root.Rotation += 0.8f;
		window.KeyThePose();
		state.SetFrame(0);
		var turn = state.Animation.Timelines.OfType<RotateTimeline>().Single();
		return (window, turn);
	}

	// Where a key (of this timeline, at this frame) is drawn in the timeline: the middle of the
	// points the graph says are on it
	private static Vector2 KeyPoint(MainWindow window, Timeline timeline, int frame)
	{
		var size = window.TimelineView.Bounds.Size;
		var on = new List<Vector2>();
		for (float y = 0; y < size.Height; y += 1)
			for (float x = 0; x < size.Width; x += 1)
			{
				window.State.SelectedKeys.Clear();
				if (window.Graph.SelectForMenu(new Vector2(x, y)) && window.State.SelectedKeys.Contains((timeline, frame)))
					on.Add(new Vector2(x, y));
			}
		window.State.SelectedKeys.Clear();
		Assert.NotEmpty(on);
		return new Vector2(on.Average(v => v.X), on.Average(v => v.Y));
	}

	private static Point InWindow(MainWindow window, Vector2 p) =>
		window.TimelineView.TranslatePoint(new Point(p.X, p.Y), window) ?? throw new InvalidOperationException();

	[AvaloniaFact]
	public void Curves_show_the_keys_which_are_dragged_and_eased_there()
	{
		var (window, turn) = Animated();
		Assert.True(window.HandleKey(Key.G, KeyModifiers.Control));
		Assert.True(window.GraphMode);
		Assert.True(window.CurvesBox.IsChecked);
		Windows.Settle(window);
		Windows.Shot(window, "timeline-curves");

		// The second key dragged up (Shift: only up and down): a bigger turn, at the same frame
		float before = turn.Track.Keys[1].Value;
		var key = KeyPoint(window, turn, 10);
		var from = InWindow(window, key);
		window.MouseDown(from, MouseButton.Left);
		window.MouseMove(new Point(from.X, from.Y - 10), RawInputModifiers.Shift);
		window.MouseMove(new Point(from.X, from.Y - 30), RawInputModifiers.Shift);
		window.MouseUp(new Point(from.X, from.Y - 30), MouseButton.Left, RawInputModifiers.Shift);
		Assert.True(turn.Track.Keys[1].Value > before + 0.01f);
		Assert.Equal(10, (int)MathF.Round(turn.Track.Keys[1].Time * window.State.Fps));
		Assert.Null(window.KeysTimeline.Range);   // (the dopesheet didn't see the drag)
		window.State.Undo.Undo();
		Assert.Equal(before, turn.Track.Keys[1].Value, 4);

		// The key menu, from the curves: eased in
		window.Graph.SelectForMenu(KeyPoint(window, turn, 0));
		Assert.NotEmpty(window.State.SelectedKeys);
		var menu = window.KeyMenu(null);
		var ease = menu.Items.OfType<MenuItem>().First(i => i.Header as string == "Bézier easing");
		ease.Items.OfType<MenuItem>().First(i => i.Header as string == "Ease in").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		Assert.Equal(CurveType.Bezier, turn.Track.Keys[0].Curve);

		// And back to the keys
		window.CurvesBox.IsChecked = false;
		Assert.False(window.GraphMode);
	}
}
