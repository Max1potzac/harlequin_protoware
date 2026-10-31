using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

// What colour layers do to colours, worked out once: as plain sums (Apply, for one colour) and as
// Skia colour filters for drawing. Hue / saturation is a colour matrix (then a lightness curve);
// the others are a curve for each of red, green and blue. Replace colour picks out pixels by
// their colour, so it's done pixel by pixel on a drawing's picture (ReplaceColor).
// Colours are red, green, blue 0..1, not premultiplied; strength 0..1 blends from no change.
public static class ColorFilters
{
	private const float LumR = 0.213f, LumG = 0.715f, LumB = 0.072f;

	public static float Luminance(Vector3 c) => c.X * LumR + c.Y * LumG + c.Z * LumB;

	private static float At(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

	// ======================= the sums =======================

	public static Vector3 Apply(Vector3 color, ColorAdjust adjust, float strength)
	{
		var v = adjust.Now;
		float s = Math.Clamp(strength * v.Mix, 0f, 1f);
		if (s <= 0) return color;
		if (adjust.Kind == ColorAdjustKind.ReplaceColor) return Replace(color, v, s);
		if (Matrix(adjust, s) is { } m)
			color = Vector3.Clamp(new Vector3(
				m[0] * color.X + m[1] * color.Y + m[2] * color.Z,
				m[3] * color.X + m[4] * color.Y + m[5] * color.Z,
				m[6] * color.X + m[7] * color.Y + m[8] * color.Z), Vector3.Zero, Vector3.One);
		if (Curve(adjust, s) is { } curve)
			color = new Vector3(curve(0, color.X), curve(1, color.Y), curve(2, color.Z));
		return color;
	}

	// Hue / saturation's 3×3 matrix (row by row), blended from no change by s; null: none
	private static float[]? Matrix(ColorAdjust adjust, float s)
	{
		if (adjust.Kind != ColorAdjustKind.HueSaturation) return null;
		var v = adjust.Now;
		float[] m;
		if (adjust.Colorize)
		{
			// Every colour that one hue: its own lightness, the hue's colour (as rich as Saturation says)
			var hue = HueColor(v.Hue);
			float lum = MathF.Max(0.05f, Luminance(hue));
			float rich = Math.Clamp((v.Saturation + 1) / 2, 0f, 1f);
			m = new float[9];
			for (int row = 0; row < 3; row++)
			{
				float f = 1 + (At(hue, row) / lum - 1) * rich;
				m[row * 3] = LumR * f;
				m[row * 3 + 1] = LumG * f;
				m[row * 3 + 2] = LumB * f;
			}
		}
		else
		{
			if (MathF.Abs(v.Hue) < 1e-4f && MathF.Abs(v.Saturation) < 1e-4f) m = Identity();
			else m = Multiply(SaturationMatrix(1 + v.Saturation), HueMatrix(v.Hue * MathF.PI / 180));
		}
		var id = Identity();
		for (int i = 0; i < 9; i++) m[i] = id[i] + (m[i] - id[i]) * s;
		return m;
	}

	private static float[] Identity() => new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

	// Round the colour wheel, keeping the brightness
	private static float[] HueMatrix(float radians)
	{
		float c = MathF.Cos(radians), n = MathF.Sin(radians);
		return new[]
		{
			LumR + c * (1 - LumR) - n * LumR,        LumG - c * LumG - n * LumG,          LumB - c * LumB + n * (1 - LumB),
			LumR - c * LumR + n * 0.143f,            LumG + c * (1 - LumG) + n * 0.140f,  LumB - c * LumB - n * 0.283f,
			LumR - c * LumR - n * (1 - LumR),        LumG - c * LumG + n * LumG,          LumB + c * (1 - LumB) + n * LumB
		};
	}

	// 0 = grey, 1 = as it is, 2 = twice as rich
	private static float[] SaturationMatrix(float amount) => new[]
	{
		LumR + (1 - LumR) * amount, LumG - LumG * amount,       LumB - LumB * amount,
		LumR - LumR * amount,       LumG + (1 - LumG) * amount, LumB - LumB * amount,
		LumR - LumR * amount,       LumG - LumG * amount,       LumB + (1 - LumB) * amount
	};

	private static float[] Multiply(float[] a, float[] b)
	{
		var m = new float[9];
		for (int r = 0; r < 3; r++)
			for (int c = 0; c < 3; c++)
				m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
		return m;
	}

	// A fully rich colour of this hue (degrees: 0 red, 120 green, 240 blue)
	public static Vector3 HueColor(float degrees)
	{
		float h = ((degrees % 360) + 360) % 360 / 60f;
		float x = 1 - MathF.Abs(h % 2 - 1);
		return (int)h switch
		{
			0 => new Vector3(1, x, 0),
			1 => new Vector3(x, 1, 0),
			2 => new Vector3(0, 1, x),
			3 => new Vector3(0, x, 1),
			4 => new Vector3(x, 0, 1),
			_ => new Vector3(1, 0, x)
		};
	}

	// The curve each of red (0), green (1), blue (2) goes through, blended from none by s; null: none
	private static Func<int, float, float>? Curve(ColorAdjust adjust, float s)
	{
		var v = adjust.Now;
		Func<int, float, float>? f = adjust.Kind switch
		{
			ColorAdjustKind.HueSaturation when MathF.Abs(v.Lightness) > 1e-4f =>
				(_, x) => v.Lightness > 0 ? x + (1 - x) * v.Lightness : x * (1 + v.Lightness),
			ColorAdjustKind.BrightnessContrast =>
				(_, x) => (x - 0.5f) * (1 + v.Contrast) + 0.5f + v.Brightness * 0.5f,
			ColorAdjustKind.Tint when adjust.KeepLight =>
				(i, x) => x * (1 + (At(v.TintColor, i) / MathF.Max(0.05f, Luminance(v.TintColor)) - 1) * v.TintAmount),
			ColorAdjustKind.Tint =>
				(i, x) => x + (At(v.TintColor, i) - x) * v.TintAmount,
			ColorAdjustKind.ColorBalance =>
				(i, x) => x + 0.5f * (At(v.Shadows, i) * (1 - x) * (1 - x) + At(v.Midtones, i) * 4 * x * (1 - x) + At(v.Highlights, i) * x * x),
			_ => null
		};
		if (f == null) return null;
		return (i, x) => Math.Clamp(x + (Math.Clamp(f(i, x), 0f, 1f) - x) * s, 0f, 1f);
	}

	// Replace colour: how near a colour is to the one picked out (1 within the tolerance, fading
	// to 0 over the soft edge), and the colour it becomes (moved by the difference, so shading stays)
	private static Vector3 Replace(Vector3 color, AdjustValues v, float s)
	{
		float distance = Vector3.Distance(color, v.From) / MathF.Sqrt(3);
		float edge = MathF.Max(1e-4f, v.Softness);
		float near = distance <= v.Tolerance ? 1f : Math.Clamp(1 - (distance - v.Tolerance) / edge, 0f, 1f);
		if (near <= 0) return color;
		return Vector3.Clamp(color + (v.To - v.From) * (near * s), Vector3.Zero, Vector3.One);
	}

	// ======================= as Skia colour filters =======================

	// The filter for this colour layer at this strength (null: it changes nothing, or it's Replace
	// colour, which isn't a filter)
	public static SKColorFilter? For(ColorAdjust adjust, float strength)
	{
		float s = Math.Clamp(strength * adjust.Now.Mix, 0f, 1f);
		if (s <= 0 || adjust.Kind == ColorAdjustKind.ReplaceColor) return null;

		SKColorFilter? matrix = null, table = null;
		if (Matrix(adjust, s) is { } m)
			matrix = SKColorFilter.CreateColorMatrix(new[]
			{
				m[0], m[1], m[2], 0, 0,
				m[3], m[4], m[5], 0, 0,
				m[6], m[7], m[8], 0, 0,
				0, 0, 0, 1, 0
			});
		if (Curve(adjust, s) is { } curve)
		{
			var alpha = new byte[256];
			var tables = new byte[3][];
			for (int i = 0; i < 3; i++) tables[i] = new byte[256];
			for (int x = 0; x < 256; x++)
			{
				alpha[x] = (byte)x;
				for (int i = 0; i < 3; i++) tables[i][x] = (byte)MathF.Round(curve(i, x / 255f) * 255);
			}
			table = SKColorFilter.CreateTable(alpha, tables[0], tables[1], tables[2]);
		}
		return Then(matrix, table);
	}

	// One filter, then the other (either may be none)
	public static SKColorFilter? Then(SKColorFilter? first, SKColorFilter? second)
	{
		if (first == null) return second;
		if (second == null) return first;
		var both = SKColorFilter.CreateCompose(second, first);   // (outer, inner)
		first.Dispose();
		second.Dispose();
		return both;
	}

	// ======================= on a drawing's picture =======================

	// A drawing's colour layer, on the picture of the layers under it (in place)
	public static void ApplyTo(SKBitmap image, ColorAdjust adjust, float strength)
	{
		if (adjust.Kind == ColorAdjustKind.ReplaceColor)
		{
			float s = Math.Clamp(strength * adjust.Now.Mix, 0f, 1f);
			if (s > 0) ReplaceColor(image, adjust.Now, s);
			return;
		}
		using var filter = For(adjust, strength);
		if (filter == null) return;
		using var copy = image.Copy();
		using var canvas = new SKCanvas(image);
		using var paint = new SKPaint { ColorFilter = filter, BlendMode = SKBlendMode.Src };
		canvas.DrawBitmap(copy, 0, 0, AssetStore.Nearest, paint);
	}

	// Replace colour, pixel by pixel (premultiplied 8-bit pixels, red-green-blue or blue-green-red)
	public static void ReplaceColor(SKBitmap image, AdjustValues v, float s)
	{
		int count = image.Width * image.Height * 4;
		if (count == 0 || image.BytesPerPixel != 4) return;
		var px = new byte[count];
		Marshal.Copy(image.GetPixels(), px, 0, count);
		bool bgr = image.ColorType == SKColorType.Bgra8888;
		int r = bgr ? 2 : 0, b = bgr ? 0 : 2;
		for (int i = 0; i < count; i += 4)
		{
			int a = px[i + 3];
			if (a == 0) continue;
			float k = 255f / a;   // un-premultiplied
			var c = new Vector3(px[i + r] * k, px[i + 1] * k, px[i + b] * k) / 255f;
			var o = Replace(c, v, s) * (a / 255f) * 255f;
			px[i + r] = (byte)MathF.Round(o.X);
			px[i + 1] = (byte)MathF.Round(o.Y);
			px[i + b] = (byte)MathF.Round(o.Z);
		}
		Marshal.Copy(px, 0, image.GetPixels(), count);
		image.NotifyPixelsChanged();
	}
}
