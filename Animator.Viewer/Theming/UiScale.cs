using System.Windows;
using System.Windows.Media;

namespace Animator.Viewer.Theming;

// How big everything is drawn (Settings → Appearance: 90 % … 200 %), on top of Windows' own
// display scaling. Dialogs scale as a whole, and grow to fit; the main window scales its panels,
// menus and bars, but not the viewport or the timeline, which draw themselves (so they stay sharp).
public interface IScalesItsParts
{
	void ScaleParts(Transform transform);
}

public static class UiScale
{
	public static readonly double[] Choices = { 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 };

	public static double Current { get; private set; } = 1.0;

	private static bool _hooked;
	private static readonly DependencyProperty AppliedProperty =
		DependencyProperty.RegisterAttached("Applied", typeof(double), typeof(UiScale), new PropertyMetadata(1.0));

	public static void Apply(double scale)
	{
		Current = Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.75, 2.5);
		if (!_hooked)
		{
			EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
			{
				if (s is Window w) Scale(w);
			}));
			_hooked = true;
		}
		if (Application.Current is { } app)
			foreach (Window window in app.Windows)
				if (window.IsLoaded) Scale(window);
	}

	private static void Scale(Window window)
	{
		double old = (double)window.GetValue(AppliedProperty);
		var transform = Current == 1.0 ? Transform.Identity : new ScaleTransform(Current, Current);
		if (window is IScalesItsParts parts)
		{
			parts.ScaleParts(transform);
		}
		else if (window.Content is FrameworkElement root)
		{
			root.LayoutTransform = transform;
			// A dialog of a set size grows (or shrinks) with what's in it
			double ratio = Current / old;
			if (window.Owner != null && Math.Abs(ratio - 1) > 1e-3)
			{
				if (!window.SizeToContent.HasFlag(SizeToContent.Width) && double.IsFinite(window.Width)) window.Width *= ratio;
				if (!window.SizeToContent.HasFlag(SizeToContent.Height) && double.IsFinite(window.Height)) window.Height *= ratio;
			}
		}
		window.SetValue(AppliedProperty, Current);
	}

	public static string Name(double scale) => $"{Math.Round(scale * 100)} %";
}
