using SkiaSharp;

namespace Animator.Viewer.Painting;

// Hue / saturation / value: the way the colour picker thinks about colours
public static class ColorMath
{
	// hue 0..360 (red, yellow, green, cyan, blue, magenta, back to red); sat and val 0..1
	public static SKColor FromHsv(double hue, double sat, double val, byte alpha = 255)
	{
		hue = (hue % 360 + 360) % 360;
		double c = val * sat;
		double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
		double m = val - c;
		var (r, g, b) = hue switch
		{
			< 60 => (c, x, 0d),
			< 120 => (x, c, 0d),
			< 180 => (0d, c, x),
			< 240 => (0d, x, c),
			< 300 => (x, 0d, c),
			_ => (c, 0d, x)
		};
		return new SKColor(ToByte(r + m), ToByte(g + m), ToByte(b + m), alpha);
	}

	public static (double Hue, double Sat, double Val) ToHsv(SKColor color)
	{
		double r = color.Red / 255.0, g = color.Green / 255.0, b = color.Blue / 255.0;
		double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
		double delta = max - min;

		double hue = 0;
		if (delta > 0)
		{
			if (max == r) hue = 60 * ((g - b) / delta % 6);
			else if (max == g) hue = 60 * ((b - r) / delta + 2);
			else hue = 60 * ((r - g) / delta + 4);
		}
		if (hue < 0) hue += 360;
		return (hue, max == 0 ? 0 : delta / max, max);
	}

	private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
