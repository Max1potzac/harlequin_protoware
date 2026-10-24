using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Animator.Core.Import;

// A motion tween ("motion object", Animate CS4 and later): curves for position, rotation, skew,
// scale and colour over the tween's span, each with an optional ease (a "time map").
// Times are in TimeScale units per second. Checked against Animate's own published frames:
// position moves along the path of its X and Y keys at an even speed, the other properties
// follow their own curves in time.
public sealed class XflMotion
{
	public float TimeScale { get; init; } = 24000;
	public float Duration { get; init; }
	public List<(string Type, int Strength)> TimeMaps { get; } = new();
	public Dictionary<string, XflMotionProperty> Properties { get; } = new(StringComparer.Ordinal);

	public static XflMotion Read(XElement core)
	{
		var motion = new XflMotion
		{
			TimeScale = Float(core, "TimeScale", 24000),
			Duration = Float(core, "duration", 0)
		};
		foreach (var map in core.Elements().Where(e => e.Name.LocalName == "TimeMap"))
			motion.TimeMaps.Add((map.Attribute("type")?.Value ?? "Quadratic", (int)MathF.Round(Float(map, "strength", 0))));
		foreach (var p in core.Descendants().Where(e => e.Name.LocalName == "Property"))
		{
			string id = p.Attribute("id")?.Value ?? "";
			if (id.Length == 0 || p.Attribute("enabled")?.Value == "0") continue;
			var property = new XflMotionProperty
			{
				Id = id,
				IgnoreTimeMap = p.Attribute("ignoreTimeMap")?.Value == "1",
				TimeMap = (int)Float(p, "TimeMapIndex", 0)
			};
			foreach (var k in p.Elements().Where(e => e.Name.LocalName == "Keyframe"))
				property.Keys.Add(new XflMotionKey(Float(k, "timevalue", 0), Pair(k, "anchor").Y, Pair(k, "previous"), Pair(k, "next")));
			property.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
			if (property.Keys.Count > 0) motion.Properties[id] = property;
		}
		return motion;
	}

	// ----- evaluating, `frame` frames into the tween -----

	private float UnitsPerFrame(float fps) => TimeScale / Math.Max(1f, fps);

	// The time a property is evaluated at: the frame's place in the tween, through its ease
	private float EasedTime(XflMotionProperty p, float frame, float fps)
	{
		float perFrame = UnitsPerFrame(fps);
		float last = MathF.Max(1f, Duration / perFrame - 1f);
		float t = Math.Clamp(frame / last, 0f, 1f);
		if (!p.IgnoreTimeMap && p.TimeMap >= 0 && p.TimeMap < TimeMaps.Count)
			t = (float)TimeMap(TimeMaps[p.TimeMap].Type, TimeMaps[p.TimeMap].Strength, t, (int)MathF.Round(last));
		return t * last * perFrame;
	}

	public float Value(string id, float frame, float fps, float fallback) =>
		Properties.TryGetValue(id, out var p) ? p.At(EasedTime(p, frame, fps)) : fallback;

	public bool Has(string id) => Properties.ContainsKey(id);

	public bool HasColor => Properties.Keys.Any(k => k.StartsWith("AdvClr_", StringComparison.Ordinal) ||
													 k is "Alpha_Amount" or "Brightness_Amount" or "Tint_Amount");

	// How far the transformation point has moved: along the path through the X and Y keys at an
	// even speed when they share their keys and ease; each on its own curve otherwise
	public Vector2 Position(float frame, float fps)
	{
		Properties.TryGetValue("Motion_X", out var x);
		Properties.TryGetValue("Motion_Y", out var y);
		if (x == null || y == null)
			return new Vector2(x == null ? 0 : x.At(EasedTime(x, frame, fps)), y == null ? 0 : y.At(EasedTime(y, frame, fps)));
		bool together = x.Keys.Count == y.Keys.Count && x.Keys.Count >= 2 && x.IgnoreTimeMap == y.IgnoreTimeMap &&
						(x.IgnoreTimeMap || x.TimeMap == y.TimeMap) &&
						x.Keys.Select(k => k.Time).SequenceEqual(y.Keys.Select(k => k.Time));
		if (!together) return new Vector2(x.At(EasedTime(x, frame, fps)), y.At(EasedTime(y, frame, fps)));

		float time = EasedTime(x, frame, fps);
		var keys = x.Keys;
		if (time <= keys[0].Time) return new Vector2(keys[0].Value, y.Keys[0].Value);
		if (time >= keys[^1].Time) return new Vector2(keys[^1].Value, y.Keys[^1].Value);
		for (int i = 0; i + 1 < keys.Count; i++)
		{
			if (time > keys[i + 1].Time) continue;
			var p0 = new Vector2(keys[i].Value, y.Keys[i].Value);
			var p3 = new Vector2(keys[i + 1].Value, y.Keys[i + 1].Value);
			var p1 = new Vector2(keys[i].Next.Y, y.Keys[i].Next.Y);
			var p2 = new Vector2(keys[i + 1].Previous.Y, y.Keys[i + 1].Previous.Y);
			float span = keys[i + 1].Time - keys[i].Time;
			return AlongPath(p0, p1, p2, p3, span <= 0 ? 1f : (time - keys[i].Time) / span);
		}
		return new Vector2(keys[^1].Value, y.Keys[^1].Value);
	}

	// The point `fraction` of the way along a cubic Bézier, measured by length
	private static Vector2 AlongPath(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float fraction)
	{
		const int steps = 64;
		Span<float> lengths = stackalloc float[steps + 1];
		var previous = p0;
		lengths[0] = 0;
		for (int i = 1; i <= steps; i++)
		{
			var point = Bezier(p0, p1, p2, p3, i / (float)steps);
			lengths[i] = lengths[i - 1] + Vector2.Distance(previous, point);
			previous = point;
		}
		float target = Math.Clamp(fraction, 0f, 1f) * lengths[steps];
		if (lengths[steps] < 1e-6f) return p0;
		for (int i = 1; i <= steps; i++)
		{
			if (lengths[i] < target) continue;
			float piece = lengths[i] - lengths[i - 1];
			float k = piece < 1e-9f ? 0 : (target - lengths[i - 1]) / piece;
			return Bezier(p0, p1, p2, p3, (i - 1 + k) / steps);
		}
		return p3;
	}

	public static Vector2 Bezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float s)
	{
		float u = 1 - s;
		return u * u * u * p0 + 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s * p3;
	}

	// The instance's placement `frame` frames in: scale, rotation and skew from their curves,
	// turned around its transformation point, which has moved by Position
	public Matrix3x2 Placement(XflInstance start, float frame, float fps)
	{
		// A property the tween doesn't have keeps the instance's own value
		const float degrees = 180f / MathF.PI;
		var (sx0, sy0, ax0, ay0) = XflMath.Decompose(start.Matrix);
		float sx = Value("Scale_X", frame, fps, sx0 * 100) / 100f;
		float sy = Value("Scale_Y", frame, fps, sy0 * 100) / 100f;
		float rotation = Value("Rotation_Z", frame, fps, ax0 * degrees) / degrees;
		float skewX = Value("Skew_X", frame, fps, (ay0 - ax0) * degrees) / degrees;
		float skewY = Value("Skew_Y", frame, fps, 0) / degrees;
		var linear = XflMath.Compose(sx, sy, rotation + skewY, rotation + skewX);
		var moved = Vector2.Transform(start.Pivot, start.Matrix) + Position(frame, fps);
		var turnedPivot = Vector2.TransformNormal(start.Pivot, linear);
		linear.Translation = moved - turnedPivot;
		return linear;
	}

	public XflColor Color(float frame, float fps)
	{
		var multiply = new Vector4(Value("AdvClr_R_Pct", frame, fps, 100), Value("AdvClr_G_Pct", frame, fps, 100),
			Value("AdvClr_B_Pct", frame, fps, 100), Value("AdvClr_A_Pct", frame, fps, 100)) / 100f;
		var offset = new Vector4(Value("AdvClr_R_Offset", frame, fps, 0), Value("AdvClr_G_Offset", frame, fps, 0),
			Value("AdvClr_B_Offset", frame, fps, 0), Value("AdvClr_A_Offset", frame, fps, 0));
		if (Has("Alpha_Amount")) multiply.W *= Value("Alpha_Amount", frame, fps, 100) / 100f;
		if (Has("Brightness_Amount"))
		{
			float b = Value("Brightness_Amount", frame, fps, 0) / 100f;
			float m = b < 0 ? 1 + b : 1 - b;
			multiply = new Vector4(multiply.X * m, multiply.Y * m, multiply.Z * m, multiply.W);
			if (b > 0) offset += new Vector4(b * 255, b * 255, b * 255, 0);
		}
		if (Has("Tint_Amount"))
		{
			float t = Value("Tint_Amount", frame, fps, 0) / 100f;
			multiply = new Vector4(multiply.X * (1 - t), multiply.Y * (1 - t), multiply.Z * (1 - t), multiply.W);
		}
		return new XflColor(multiply, offset);
	}

	// ----- eases ("time maps"): progress 0..1 at a moment 0..1 of the tween -----

	// Measured formulas (JPEXS's native Animate captures): Quadratic and DualQuadratic blend a
	// curve into straight progress by their strength; Cubic to Quintic (and Dual) likewise with
	// their power; Spring and Bounce wobble. Anything else goes at an even speed.
	public static double TimeMap(string type, int strength, double t, int frames)
	{
		double s = strength / 100.0;
		switch (type)
		{
			case "Quadratic":
				return t + s * t * (1 - t);
			case "DualQuadratic":
				return t + s * (t < 0.5 ? t : 1 - t) * (1 - 2 * t);
			case "Cubic" or "Quartic" or "Quintic" or "DualCubic" or "DualQuartic" or "DualQuintic":
				int n = type.EndsWith("Cubic") ? 3 : type.EndsWith("Quartic") ? 4 : 5;
				double q = type.StartsWith("Dual")
					? t <= 0.5 ? 0.5 * (1 - Math.Pow(1 - 2 * t, n)) : 0.5 * (1 + Math.Pow(2 * t - 1, n))
					: 1 - Math.Pow(1 - t, n);
				return t + s * (q - t);
			case "Spring" when strength > 0:
				double first = 0.4 / strength;
				if (t <= first) return Math.Sin(Math.PI * t / (2 * first));
				double u = 2 * strength * (t - first) / (1 - first);
				return 0.7 + 0.3 * Math.Exp(-u / 2) * Math.Cos(Math.PI * u);
			case "Bounce" or "BounceIn" when strength > 0 && frames > 0:
				double harmonic = 0;
				for (int j = 1; j <= strength; j++) harmonic += 1.0 / j;
				double frame = t * frames;
				int start = 0;
				for (int j = 1; j <= strength; j++)
				{
					int width = Math.Min((int)Math.Round(frames / (j * harmonic)), frames - start);
					if (width <= 0) break;
					if (frame <= start + width + 1e-9)
					{
						double v = Math.Clamp((frame - start) / width, 0, 1);
						if (type == "BounceIn" && j == 1) return v * v;
						double parabola = Math.Exp(1 - j) * 4 * v * (1 - v);
						return type == "BounceIn" ? 1 - parabola : parabola;
					}
					start += width;
				}
				return type == "BounceIn" ? 1 : 0;
			default:
				return t;
		}
	}

	private static float Float(XElement e, string name, float fallback) =>
		e.Attribute(name)?.Value is { Length: > 0 } v && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)
			? f
			: fallback;

	// "time,value"
	private static Vector2 Pair(XElement e, string name)
	{
		var parts = (e.Attribute(name)?.Value ?? "0,0").Split(',');
		float Part(int i) => i < parts.Length && float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f) ? f : 0;
		return new Vector2(Part(0), Part(1));
	}
}

public sealed class XflMotionProperty
{
	public string Id { get; init; } = "";
	public bool IgnoreTimeMap { get; init; }
	public int TimeMap { get; init; }
	public List<XflMotionKey> Keys { get; } = new();

	// Its value at a time: between keys, along the Bézier their handles make (in time and value)
	public float At(float time)
	{
		if (time <= Keys[0].Time) return Keys[0].Value;
		if (time >= Keys[^1].Time) return Keys[^1].Value;
		for (int i = 0; i + 1 < Keys.Count; i++)
		{
			var a = Keys[i];
			var b = Keys[i + 1];
			if (time > b.Time) continue;
			var p0 = new Vector2(a.Time, a.Value);
			var p1 = new Vector2(a.Time + a.Next.X, a.Next.X == 0 && a.Next.Y == 0 ? a.Value : a.Next.Y);
			var p2 = new Vector2(b.Time + b.Previous.X, b.Previous.X == 0 && b.Previous.Y == 0 ? b.Value : b.Previous.Y);
			var p3 = new Vector2(b.Time, b.Value);
			if (a.Next.X == 0) p1.Y = a.Value;       // no handle: straight out of the key
			if (b.Previous.X == 0) p2.Y = b.Value;
			// The curve's parameter where its time reaches `time` (time only ever increases along it)
			float lo = 0, hi = 1;
			for (int step = 0; step < 40; step++)
			{
				float mid = (lo + hi) / 2;
				if (XflMotion.Bezier(p0, p1, p2, p3, mid).X < time) lo = mid;
				else hi = mid;
			}
			return XflMotion.Bezier(p0, p1, p2, p3, (lo + hi) / 2).Y;
		}
		return Keys[^1].Value;
	}
}

// A key of a motion property: its value, and the handles before and after it (time offset, value)
public readonly record struct XflMotionKey(float Time, float Value, Vector2 Previous, Vector2 Next);
