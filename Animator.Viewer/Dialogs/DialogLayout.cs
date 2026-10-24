using System.Windows;

namespace Animator.Viewer.Dialogs;

// Big dialogs on small (or scaled-up) screens: no bigger than the screen's work area, and moved
// onto it once shown, so nothing ends up off the edge where it can't be reached
internal static class DialogLayout
{
	public static void FitToScreen(Window window)
	{
		var area = SystemParameters.WorkArea;
		window.MinWidth = Math.Min(window.MinWidth, area.Width);
		window.MinHeight = Math.Min(window.MinHeight, area.Height);
		window.Width = Math.Min(window.Width, area.Width);
		window.Height = Math.Min(window.Height, area.Height);
		window.Loaded += (_, _) =>
		{
			window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.ActualWidth));
			window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.ActualHeight));
		};
	}
}
