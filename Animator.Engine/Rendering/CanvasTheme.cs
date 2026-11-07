using SkiaSharp;

namespace Animator.Viewer.Rendering;

// The theme's colours for what's drawn with Skia (the timelines, the graph, the paint canvas, the
// rulers), looked up by name as they draw, so a theme switch shows at once. The editor hands in
// its theme's (Colour); without one, the Dark theme's.
public static class CanvasTheme
{
	private static readonly Dictionary<string, SKColor> Dark = new()
	{
		["Viewport"] = SKColor.Parse("#282C34"),
		["Canvas"] = SKColor.Parse("#1E2127"),
		["CanvasText"] = SKColor.Parse("#C8C8C8"),
		["CanvasLine"] = SKColor.Parse("#5AFFFFFF"),
		["Playhead"] = SKColor.Parse("#FF5A5A"),
	};

	public static Func<string, SKColor> Colour { get; set; } = name => Dark.TryGetValue(name, out var c) ? c : SKColors.Magenta;

	public static SKColor Sk(string name) => Colour(name);

	// The colour with this opacity instead of its own (0..255)
	public static SKColor SkAlpha(string name, byte alpha) => Colour(name).WithAlpha(alpha);
}
