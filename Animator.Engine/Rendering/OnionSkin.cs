using SkiaSharp;

namespace Animator.Viewer.Rendering;

// Onion skin settings: faint, tinted copies of the neighbouring frames.
// In the viewport: the whole rig at earlier / later frames. In the paint editor
// ("light table"): the part's previous / next drawings, under the one you paint.
public sealed class OnionSkin
{
	public bool Enabled { get; set; }
	public int Before { get; set; } = 2;   // how many earlier frames (or drawings) to show
	public int After { get; set; } = 2;    // how many later ones
	public int Step { get; set; } = 1;     // viewport: every Nth frame (2 = animating on twos)

	// Red = before, blue = after (easy to tell apart, also with red-green colour blindness)
	public static readonly SKColor BeforeTint = new(235, 60, 60);
	public static readonly SKColor AfterTint = new(50, 140, 255);

	// Nearer ghosts are stronger: with 3 ghosts, 50%, 33%, 17%
	public static float Opacity(int distance, int count) =>
		count <= 0 ? 0f : 0.5f * (count - distance + 1) / count;

	// A ghost's colours pulled toward the tint (keeps its shape: transparent stays transparent)
	public static SKColorFilter TintFilter(bool before) =>
		SKColorFilter.CreateBlendMode((before ? BeforeTint : AfterTint).WithAlpha(170), SKBlendMode.SrcATop);

	// Paint colour that makes a drawn image this see-through
	public static SKColor Fade(float opacity) =>
		SKColors.White.WithAlpha((byte)(Math.Clamp(opacity, 0f, 1f) * 255));
}
