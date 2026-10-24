using System.Globalization;
using System.Numerics;

namespace Animator.Core.Import;

// A piece of an outline: a straight line, or a quadratic curve through Control, to End
public readonly record struct XflCurve(bool Quad, Vector2 Control, Vector2 End);

// A run of outline from Start; closed when it ends where it started
public sealed class XflContour
{
	public Vector2 Start { get; set; }
	public List<XflCurve> Curves { get; } = new();
	public Vector2 End => Curves.Count == 0 ? Start : Curves[^1].End;
	public bool Closed => Curves.Count > 0 && Vector2.DistanceSquared(Start, End) < 1e-8f;

	public XflContour Reversed()
	{
		var reversed = new XflContour { Start = End };
		for (int i = Curves.Count - 1; i >= 0; i--)
		{
			var from = i == 0 ? Start : Curves[i - 1].End;
			reversed.Curves.Add(new XflCurve(Curves[i].Quad, Curves[i].Control, from));
		}
		return reversed;
	}
}

// Animate's edge format, made into fills and strokes. Each <Edge> holds pieces of outline with
// the fill on their left and on their right; a fill's pieces, all turned to have it on the left,
// join end to end into closed loops (holes included). Strokes run along the pieces.
public static class XflShapeGeometry
{
	public static (List<(int Fill, List<XflContour> Loops)> Fills, List<(int Stroke, List<XflContour> Runs)> Strokes) Build(XflShape shape)
	{
		var fills = new Dictionary<int, List<XflContour>>();
		var strokes = new Dictionary<int, List<XflContour>>();
		foreach (var edge in shape.Edges)
		{
			foreach (var piece in Parse(edge.Path))
			{
				if (edge.LeftFill > 0) Add(fills, edge.LeftFill, piece);
				if (edge.RightFill > 0) Add(fills, edge.RightFill, piece.Reversed());
				if (edge.Stroke > 0) Add(strokes, edge.Stroke, piece);
			}
		}
		var filled = fills.Where(f => shape.Fills.ContainsKey(f.Key)).OrderBy(f => f.Key)
			.Select(f => (f.Key, Join(f.Value))).ToList();
		var stroked = strokes.Where(s => shape.Strokes.ContainsKey(s.Key)).OrderBy(s => s.Key)
			.Select(s => (s.Key, Join(s.Value))).ToList();
		return (filled, stroked);
	}

	private static void Add(Dictionary<int, List<XflContour>> by, int style, XflContour piece)
	{
		if (!by.TryGetValue(style, out var list)) by[style] = list = new List<XflContour>();
		list.Add(piece);
	}

	private static (long, long) Key(Vector2 p) => ((long)MathF.Round(p.X * 1000), (long)MathF.Round(p.Y * 1000));

	// Pieces joined end to end. A fill's pieces always close (each corner has as many pieces
	// leaving as arriving); one that doesn't is closed with a straight line when it's filled.
	private static List<XflContour> Join(List<XflContour> pieces)
	{
		var starting = new Dictionary<(long, long), Stack<XflContour>>();
		var result = new List<XflContour>();
		foreach (var piece in pieces)
		{
			if (piece.Closed) result.Add(piece);
			else
			{
				var key = Key(piece.Start);
				if (!starting.TryGetValue(key, out var stack)) starting[key] = stack = new Stack<XflContour>();
				stack.Push(piece);
			}
		}
		foreach (var piece in pieces)
		{
			if (piece.Closed) continue;
			var key = Key(piece.Start);
			if (!starting.TryGetValue(key, out var stack) || !stack.Contains(piece)) continue;   // already used
			RemoveFrom(stack, piece);
			var joined = new XflContour { Start = piece.Start };
			joined.Curves.AddRange(piece.Curves);
			while (!joined.Closed && starting.TryGetValue(Key(joined.End), out var nexts) && nexts.Count > 0)
				joined.Curves.AddRange(nexts.Pop().Curves);
			result.Add(joined);
		}
		return result;
	}

	private static void RemoveFrom(Stack<XflContour> stack, XflContour piece)
	{
		var kept = stack.Where(p => p != piece).Reverse().ToList();
		stack.Clear();
		foreach (var p in kept) stack.Push(p);
	}

	// The edge format: "!x y" moves, "|x y" or "/x y" draws a line, "[cx cy x y" or "]cx cy x y"
	// a quadratic curve; "S1".."S7" are selection hints. Numbers are twips (1/20 pixel), plain
	// or as "#" hex fixed point (24.8, two's complement).
	public static List<XflContour> Parse(string edges)
	{
		var pieces = new List<XflContour>();
		XflContour? current = null;
		var at = Vector2.Zero;
		int i = 0;
		while (i < edges.Length)
		{
			char c = edges[i];
			if (c is '!' or '|' or '/' or '[' or ']')
			{
				i++;
				if (c == '!')
				{
					if (!TryPoint(edges, ref i, out var to)) break;
					if (current == null || Vector2.DistanceSquared(to, at) > 1e-10f)
					{
						if (current is { Curves.Count: > 0 }) pieces.Add(current);
						current = new XflContour { Start = to };
					}
					at = to;
				}
				else if (c is '|' or '/')
				{
					if (!TryPoint(edges, ref i, out var to)) break;
					current ??= new XflContour { Start = at };
					current.Curves.Add(new XflCurve(false, to, to));
					at = to;
				}
				else
				{
					if (!TryPoint(edges, ref i, out var control) || !TryPoint(edges, ref i, out var to)) break;
					current ??= new XflContour { Start = at };
					current.Curves.Add(new XflCurve(true, control, to));
					at = to;
				}
			}
			else if (c == 'S' && i + 1 < edges.Length && char.IsDigit(edges[i + 1]))
			{
				i += 2;
			}
			else i++;
		}
		if (current is { Curves.Count: > 0 }) pieces.Add(current);
		return pieces;
	}

	private static bool TryPoint(string s, ref int i, out Vector2 point)
	{
		point = default;
		if (!TryNumber(s, ref i, out float x) || !TryNumber(s, ref i, out float y)) return false;
		point = new Vector2(x, y);
		return true;
	}

	private static bool TryNumber(string s, ref int i, out float value)
	{
		value = 0;
		while (i < s.Length && (s[i] == ' ' || s[i] == ',' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
		if (i >= s.Length) return false;
		int start = i;
		if (s[i] == '#')
		{
			i++;
			int whole = i;
			while (i < s.Length && Uri.IsHexDigit(s[i])) i++;
			string integer = s[whole..i];
			string fraction = "";
			if (i < s.Length && s[i] == '.')
			{
				int f = ++i;
				while (i < s.Length && Uri.IsHexDigit(s[i])) i++;
				fraction = s[f..i];
			}
			if (integer.Length == 0 || integer.Length > 6) return false;
			fraction = (fraction + "00")[..2];
			uint raw = uint.Parse(integer.PadLeft(6, '0') + fraction, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
			value = unchecked((int)raw) / 256f / 20f;
			return true;
		}
		if (s[i] == '-' || s[i] == '+') i++;
		while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
		if (i < s.Length && (s[i] == 'e' || s[i] == 'E') && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] == '-'))
		{
			i += 2;
			while (i < s.Length && char.IsDigit(s[i])) i++;
		}
		if (!float.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float twips)) return false;
		value = twips / 20f;
		return true;
	}
}
