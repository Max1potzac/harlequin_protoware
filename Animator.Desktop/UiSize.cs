using Avalonia.Controls;
using Avalonia.Media;

namespace Animator.Desktop;

// How big the panels and dialogs are drawn (Settings → Appearance: 90 % … 200 %), on top of the
// system's own display scaling. Dialogs scale as a whole, and a dialog of a set size grows with
// what's in it; the main window scales its menus, bars and panels, but not the viewport or the
// timeline, which draw themselves at the screen's own size. (SkiaView draws a scaled preview at
// the pixels it covers, so pictures stay sharp.)
public static class UiSize
{
	public static readonly double[] Choices = { 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 };

	public static double Current { get; private set; } = 1.0;

	public static event Action? Changed;

	public static void Set(double scale)
	{
		double s = Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.75, 2.5);
		if (Math.Abs(s - Current) < 1e-6) return;
		Current = s;
		Changed?.Invoke();
	}

	public static string Name(double scale) => $"{Math.Round(scale * 100)} %";

	public static ITransform? Transform => Math.Abs(Current - 1.0) < 1e-6 ? null : new ScaleTransform(Current, Current);

	// A part of a window drawn at the size: wrapped once, then follows the setting
	public static LayoutTransformControl Scaled(Control part)
	{
		var wrapper = new LayoutTransformControl { Child = part, LayoutTransform = Transform };
		Changed += () => wrapper.LayoutTransform = Transform;
		return wrapper;
	}

	// A dialog drawn at the size (called once its content is in place, before it's shown)
	public static void Fit(Window dialog)
	{
		if (Math.Abs(Current - 1.0) < 1e-6 || dialog.Content is not Control content) return;
		dialog.Content = null;   // (let go of it first: a control has one parent)
		dialog.Content = new LayoutTransformControl { Child = content, LayoutTransform = Transform };
		if (dialog.SizeToContent is not (SizeToContent.Width or SizeToContent.WidthAndHeight) && double.IsFinite(dialog.Width)) dialog.Width *= Current;
		if (dialog.SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight) && double.IsFinite(dialog.Height)) dialog.Height *= Current;
		if (double.IsFinite(dialog.MaxWidth)) dialog.MaxWidth *= Current;
		if (dialog.MinWidth > 0) dialog.MinWidth *= Current;
	}
}
