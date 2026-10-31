using System.Numerics;

namespace Animator.Core.Assets;

// What a colour layer does to the colours under it
public enum ColorAdjustKind
{
	HueSaturation,        // round the colour wheel, richer or greyer, lighter or darker (or all one hue)
	BrightnessContrast,
	Tint,                 // towards a colour: as a coloured filter (keeps the light), or a flash of it
	ColorBalance,         // the shadows, midtones and highlights each pushed towards a colour
	ReplaceColor          // one colour (within a tolerance, with a soft edge) into another: drawings only
}

// The numbers a colour layer works with: its kind uses some of them. Keys hold all of them and
// blend them from key to key (so a hue can shift back and forth). Hue in degrees; the rest 0..1,
// or -1..1 where they go both ways; colours are red, green, blue 0..1.
public readonly record struct AdjustValues(
	float Mix,                                        // how strongly: 0 = not at all, 1 = fully
	float Hue, float Saturation, float Lightness,
	float Brightness, float Contrast,
	Vector3 TintColor, float TintAmount,
	Vector3 Shadows, Vector3 Midtones, Vector3 Highlights,   // red, green, blue pushes, -1..1 each
	Vector3 From, Vector3 To, float Tolerance, float Softness)
{
	public static AdjustValues Default { get; } = new(
		1f, 0f, 0f, 0f, 0f, 0f,
		new Vector3(1f, 0.55f, 0.2f), 0.5f,
		Vector3.Zero, Vector3.Zero, Vector3.Zero,
		new Vector3(1f, 0f, 0f), new Vector3(0.2f, 0.8f, 0.3f), 0.15f, 0.15f);

	public static AdjustValues Lerp(AdjustValues a, AdjustValues b, float t)
	{
		float F(float x, float y) => x + (y - x) * t;
		return new(F(a.Mix, b.Mix), F(a.Hue, b.Hue), F(a.Saturation, b.Saturation), F(a.Lightness, b.Lightness),
			F(a.Brightness, b.Brightness), F(a.Contrast, b.Contrast),
			Vector3.Lerp(a.TintColor, b.TintColor, t), F(a.TintAmount, b.TintAmount),
			Vector3.Lerp(a.Shadows, b.Shadows, t), Vector3.Lerp(a.Midtones, b.Midtones, t), Vector3.Lerp(a.Highlights, b.Highlights, t),
			Vector3.Lerp(a.From, b.From, t), Vector3.Lerp(a.To, b.To, t), F(a.Tolerance, b.Tolerance), F(a.Softness, b.Softness));
	}

	// As saved: the numbers in this order
	public float[] ToArray() => new[]
	{
		Mix, Hue, Saturation, Lightness, Brightness, Contrast,
		TintColor.X, TintColor.Y, TintColor.Z, TintAmount,
		Shadows.X, Shadows.Y, Shadows.Z, Midtones.X, Midtones.Y, Midtones.Z, Highlights.X, Highlights.Y, Highlights.Z,
		From.X, From.Y, From.Z, To.X, To.Y, To.Z, Tolerance, Softness
	};

	// (missing numbers, from a shorter list, are the defaults)
	public static AdjustValues FromArray(IReadOnlyList<float>? v)
	{
		var d = Default.ToArray();
		float At(int i) => v != null && i < v.Count && float.IsFinite(v[i]) ? v[i] : d[i];
		Vector3 V(int i) => new(At(i), At(i + 1), At(i + 2));
		return new(At(0), At(1), At(2), At(3), At(4), At(5), V(6), At(9), V(10), V(13), V(16), V(19), V(22), At(25), At(26));
	}
}

// A colour layer's settings: what kind it is, its numbers at rest (Setup) and as an animation
// has them now (Now, back to Setup every frame), and its switches (not keyed)
public sealed class ColorAdjust
{
	public ColorAdjustKind Kind { get; set; }
	public bool Colorize { get; set; }          // hue / saturation: everything that one hue
	public bool KeepLight { get; set; } = true; // tint: a coloured filter (else: towards the colour, a flash)
	public AdjustValues Setup { get; set; } = AdjustValues.Default;
	public AdjustValues Now { get; set; } = AdjustValues.Default;

	public ColorAdjust() { }
	public ColorAdjust(ColorAdjustKind kind) => Kind = kind;

	public ColorAdjust Clone() => new()
	{
		Kind = Kind, Colorize = Colorize, KeepLight = KeepLight, Setup = Setup, Now = Now
	};

	public static string KindName(ColorAdjustKind kind) => kind switch
	{
		ColorAdjustKind.HueSaturation => "Hue / saturation",
		ColorAdjustKind.BrightnessContrast => "Brightness / contrast",
		ColorAdjustKind.Tint => "Tint",
		ColorAdjustKind.ColorBalance => "Colour balance",
		_ => "Replace colour"
	};
}
