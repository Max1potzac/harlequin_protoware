namespace Animator.Core.Keyframing;

// How a stretch between two keys eases: a cubic Bézier from (0, 0) to (1, 1) through two handles,
// across = time from one key to the next (0..1), up = how far the value has got (0..1, or beyond
// for an overshoot). The same curve eases every part of a value (x and y of a move, say).
public readonly record struct Bezier(float X1, float Y1, float X2, float Y2)
{
	public static Bezier EaseIn { get; } = new(0.42f, 0f, 1f, 1f);
	public static Bezier EaseOut { get; } = new(0f, 0f, 0.58f, 1f);
	public static Bezier EaseInOut { get; } = new(0.42f, 0f, 0.58f, 1f);
	public static Bezier Overshoot { get; } = new(0.34f, 1.56f, 0.64f, 1f);    // past the next value, then back
	public static Bezier Anticipate { get; } = new(0.36f, 0f, 0.66f, -0.56f);  // back first, then on (the overshoot's mirror)

	// The handles that draw the other curves (Linear, and Smooth's ease in and out)
	public static Bezier Straight { get; } = new(1 / 3f, 1 / 3f, 2 / 3f, 2 / 3f);
	public static Bezier SmoothLike { get; } = new(1 / 3f, 0f, 2 / 3f, 1f);

	// Time can't run backwards: the handles stay within the stretch, across
	public Bezier Clamped => this with { X1 = Math.Clamp(X1, 0f, 1f), X2 = Math.Clamp(X2, 0f, 1f) };

	// How far the value has got, a fraction t of the way from one key to the next
	public float Ease(float t)
	{
		if (t <= 0) return 0;
		if (t >= 1) return 1;
		var c = Clamped;
		float u = t;   // the curve's own parameter: where its x is t
		for (int i = 0; i < 8; i++)
		{
			float x = Coord(c.X1, c.X2, u) - t;
			if (MathF.Abs(x) < 1e-6f) return Coord(c.Y1, c.Y2, u);
			float dx = Slope(c.X1, c.X2, u);
			if (MathF.Abs(dx) < 1e-6f) break;
			u = Math.Clamp(u - x / dx, 0f, 1f);
		}
		float lo = 0, hi = 1;   // (Newton didn't settle: halve the way there)
		for (int i = 0; i < 40; i++)
		{
			u = (lo + hi) / 2;
			if (Coord(c.X1, c.X2, u) < t) lo = u; else hi = u;
		}
		return Coord(c.Y1, c.Y2, u);
	}

	// One coordinate of the curve at u (it starts at 0 and ends at 1), and its slope
	private static float Coord(float p1, float p2, float u)
	{
		float v = 1 - u;
		return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
	}

	private static float Slope(float p1, float p2, float u)
	{
		float v = 1 - u;
		return 3 * v * v * p1 + 6 * v * u * (p2 - p1) + 3 * u * u * (1 - p2);
	}

	public float[] ToArray() => new[] { X1, Y1, X2, Y2 };

	public static Bezier? FromArray(IReadOnlyList<float>? v) =>
		v is { Count: >= 4 } ? new Bezier(v[0], v[1], v[2], v[3]) : null;
}
