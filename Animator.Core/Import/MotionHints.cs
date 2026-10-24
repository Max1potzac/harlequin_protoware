using System.Numerics;

namespace Animator.Core.Import;

// What an animation says about how two layers move against each other, for suggesting a rig
public interface IRigMotion
{
	// How surely they're jointed: null = the animation can't tell (they never move against each
	// other); 0..1 = how well a point they share stays put on both while they move
	// (RigSuggester.ClearJoint and above: clearly a joint)
	double? Strength(int a, int b);

	// Where they turn against each other (canvas pixels), when that's clear
	Vector2? Joint(int a, int b);

	// Whether they move against each other at all
	bool MovesAgainst(int a, int b);

	// Whether they move as one: on stage together, never moving against each other, in an
	// animation where things do move (a hat on a head)
	bool MovesAsOne(int a, int b);
}

// For each pair of parts on stage together, the point that stays put on both while they move
// (a joint), found by least squares over every frame. How well one fits says how surely they're
// jointed there; parts that never move against each other say nothing either way.
public sealed class MotionHints
{
	public int Count { get; }
	private readonly double?[,] _quality;       // 0..1, null = no telling
	private readonly Vector2?[,] _joint;        // in the object, at the setup frame
	private readonly bool[,] _moves;
	private readonly bool[,] _rigid;            // shown together, never moving against each other

	private MotionHints(int n)
	{
		Count = n;
		_quality = new double?[n, n];
		_joint = new Vector2?[n, n];
		_moves = new bool[n, n];
		_rigid = new bool[n, n];
	}

	public double? Quality(int a, int b) => _quality[a, b];
	public Vector2? Joint(int a, int b) => _joint[a, b];
	public bool MovesAgainst(int a, int b) => _moves[a, b];

	// Shown together (on a few frames at least) and never moving against each other
	public bool Rigid(int a, int b) => _rigid[a, b];

	// Whether anything moves against anything else: if not, the animation says nothing
	public bool AnythingMoves { get; private set; }

	// placements[i][f]: part i's own space → the object at frame f (null = not shown); bounds[i]:
	// the part's drawing in its own space (min, max); setup: the frame the joints are wanted at
	public static MotionHints Fit(IReadOnlyList<Matrix3x2?[]> placements, IReadOnlyList<(Vector2 Min, Vector2 Max)> bounds, int setup)
	{
		int n = placements.Count;
		var hints = new MotionHints(n);
		var size = new float[n];
		for (int i = 0; i < n; i++)
		{
			var p = placements[i][setup] ?? placements[i].FirstOrDefault(x => x != null);
			float scale = p is { } m ? MathF.Sqrt(MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21)) : 1f;
			size[i] = MathF.Max(1f, Vector2.Distance(bounds[i].Min, bounds[i].Max) * scale);
		}
		for (int a = 0; a < n; a++)
			for (int b = a + 1; b < n; b++)
			{
				var (quality, joint, moves, rigid) = FitPair(placements[a], placements[b], bounds[a], bounds[b], size[a], size[b], setup);
				hints._quality[a, b] = hints._quality[b, a] = quality;
				hints._joint[a, b] = hints._joint[b, a] = joint;
				hints._moves[a, b] = hints._moves[b, a] = moves;
				hints._rigid[a, b] = hints._rigid[b, a] = rigid;
				hints.AnythingMoves |= moves;
			}
		return hints;
	}

	private static (double? Quality, Vector2? Joint, bool Moves, bool Rigid) FitPair(Matrix3x2?[] pa, Matrix3x2?[] pb,
		(Vector2 Min, Vector2 Max) boundsA, (Vector2 Min, Vector2 Max) boundsB, float sizeA, float sizeB, int setup)
	{
		// b's space → a's space, on every frame both are shown
		var relative = new List<Matrix3x2>();
		for (int f = 0; f < pa.Length && f < pb.Length; f++)
			if (pa[f] is { } ma && pb[f] is { } mb && Matrix3x2.Invert(ma, out var toA))
				relative.Add(mb * toA);
		if (relative.Count < 3) return (null, null, false, false);

		// How much b turns and moves in a's space
		var middleB = (boundsB.Min + boundsB.Max) / 2;
		float first = MathF.Atan2(relative[0].M12, relative[0].M11), angle = first, minAngle = first, maxAngle = first;
		var positions = new List<Vector2>(relative.Count);
		foreach (var r in relative)
		{
			float a = MathF.Atan2(r.M12, r.M11);
			angle += XflMath.Mod(a - angle + MathF.PI, 2 * MathF.PI) - MathF.PI;   // unwrapped
			minAngle = MathF.Min(minAngle, angle);
			maxAngle = MathF.Max(maxAngle, angle);
			positions.Add(Vector2.Transform(middleB, r));
		}
		var mean = positions.Aggregate(Vector2.Zero, (sum, point) => sum + point) / positions.Count;
		float drift = positions.Max(point => Vector2.Distance(point, mean));
		float turn = maxAngle - minAngle;
		float scaleA = pa[setup] is { } sa ? MathF.Sqrt(MathF.Abs(sa.M11 * sa.M22 - sa.M12 * sa.M21)) : 1f;
		float small = MathF.Min(sizeA, sizeB);
		if (turn < 2 * MathF.PI / 180 && drift * scaleA < MathF.Max(0.5f, 0.01f * small)) return (null, null, false, true);   // they move as one

		// The point q (b's space) landing on the same point p (a's space) on every frame:
		// minimise Σ |q·R − p|², with a faint pull of q to b's middle so it's always defined
		var normal = new double[4, 4];
		var rhs = new double[4];
		foreach (var r in relative)
		{
			Accumulate(normal, rhs, new double[] { r.M11, r.M21, -1, 0 }, -r.M31);
			Accumulate(normal, rhs, new double[] { r.M12, r.M22, 0, -1 }, -r.M32);
		}
		double pull = 1e-4 * relative.Count;
		normal[0, 0] += pull;
		normal[1, 1] += pull;
		rhs[0] += pull * middleB.X;
		rhs[1] += pull * middleB.Y;
		normal[2, 2] += 1e-9;
		normal[3, 3] += 1e-9;
		var x = Solve(normal, rhs);
		if (x == null) return (null, null, true, false);
		var q = new Vector2((float)x[0], (float)x[1]);
		var p = new Vector2((float)x[2], (float)x[3]);
		double squares = relative.Sum(each => (double)Vector2.DistanceSquared(Vector2.Transform(q, each), p));
		float rms = (float)Math.Sqrt(squares / relative.Count) * scaleA;   // in the object's units

		// A good joint fits within a little of both parts' size and lies on or near both
		float tolerance = MathF.Max(1.5f, 0.04f * small);
		double quality = Math.Exp(-Math.Pow(rms / tolerance, 2));
		if (turn < 5 * MathF.PI / 180) quality *= turn / (5 * MathF.PI / 180);   // barely turning: its centre can't be told
		if (Outside(p, boundsA) > 0.25f * Vector2.Distance(boundsA.Min, boundsA.Max) ||
			Outside(q, boundsB) > 0.25f * Vector2.Distance(boundsB.Min, boundsB.Max))
			quality *= 0.25;

		Vector2? joint = pb[setup] is { } atSetup ? Vector2.Transform(q, atSetup) : null;
		return (quality, joint, true, false);
	}

	// How far a point is outside a box (0 inside)
	private static float Outside(Vector2 p, (Vector2 Min, Vector2 Max) box) =>
		Vector2.Distance(p, Vector2.Clamp(p, Vector2.Min(box.Min, box.Max), Vector2.Max(box.Min, box.Max)));

	private static void Accumulate(double[,] normal, double[] rhs, double[] row, double value)
	{
		for (int i = 0; i < 4; i++)
		{
			rhs[i] += row[i] * value;
			for (int j = 0; j < 4; j++) normal[i, j] += row[i] * row[j];
		}
	}

	// Gaussian elimination with partial pivoting (null if singular)
	private static double[]? Solve(double[,] a, double[] b)
	{
		int n = b.Length;
		var m = (double[,])a.Clone();
		var v = (double[])b.Clone();
		for (int col = 0; col < n; col++)
		{
			int pivot = col;
			for (int row = col + 1; row < n; row++)
				if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
			if (Math.Abs(m[pivot, col]) < 1e-12) return null;
			if (pivot != col)
			{
				for (int k = 0; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
				(v[col], v[pivot]) = (v[pivot], v[col]);
			}
			for (int row = col + 1; row < n; row++)
			{
				double factor = m[row, col] / m[col, col];
				for (int k = col; k < n; k++) m[row, k] -= factor * m[col, k];
				v[row] -= factor * v[col];
			}
		}
		var x = new double[n];
		for (int row = n - 1; row >= 0; row--)
		{
			double sum = v[row];
			for (int k = row + 1; k < n; k++) sum -= m[row, k] * x[k];
			x[row] = sum / m[row, row];
		}
		return x;
	}
}
