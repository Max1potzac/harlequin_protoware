using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The bar over the timeline, as the Windows editor has it: across the whole window, in one row
// that wraps. The play buttons first; then, inside an object, its animation (which one, new, a
// copy, deleted, its name, length and looping, auto key and Key, sound, the mixer, the curves,
// which rows) and Keys ▾; at scene level, the scene (its length, loop and stage); then for both
// the frame rate, the speed, playback and onion skin, and what an action had to say.
public sealed partial class MainWindow
{
	private Control BuildTimelineBar()
	{
		BuildTimelineTools();
		var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2) };
		void Add(Control c, double gap = 6)
		{
			c.Margin = new Thickness(0, 3, gap, 3);
			if (c is TextBlock t) t.VerticalAlignment = VerticalAlignment.Center;
			bar.Children.Add(c);
		}
		static Control Divider() => new Border { Width = 1, Margin = new Thickness(4, 6), Background = Brushes.Gray, Opacity = 0.5 };

		foreach (var b in BuildTransport()) Add(b, 2);
		Add(new Border { Width = 6 });
		var animation = BuildAnimationControls();   // (_animSection: hidden at scene level)
		animation.Insert(0, _keysButton);
		animation.Insert(1, Divider());
		foreach (var c in animation) Add(c);
		foreach (var c in BuildSceneControls()) Add(c);
		Add(Divider());
		Add(new TextBlock { Text = "FPS" }, 4);
		ToolTip.SetTip(AnimFps, "Frames per second for the whole project. Timing is kept in seconds.");
		Add(AnimFps, 12);
		Add(new TextBlock { Text = "Speed" }, 4);
		Add(SpeedBox);
		Add(_playbackButton, 12);
		foreach (var c in _onionTools) Add(c, 4);
		Add(new Border { Width = 8 });
		Add(_animHint);
		Add(_sceneHint);
		_timelineBar = UiSize.Scaled(bar);
		return _timelineBar;
	}
}
