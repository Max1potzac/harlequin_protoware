using System.Numerics;

namespace Animator.Viewer.Tools;

// Snapping to the grid and the guides while dragging: a point brought near a grid line or a
// guide lands on it, across and up and down each on its own (so near a crossing, on the
// crossing). For a part, its pivot and the corners of its box all count: whichever is nearest a
// line lands on it, and the rest move along. Points are in the units the panels measure in: the
// open object's, or (at scene level) the scene's, the same ones the grid and guides are drawn in.
public sealed class Snapping
{
	public bool ToGrid { get; set; }
	public bool ToGuides { get; set; } = true;
	public float GridSize { get; set; } = 10f;
	public IReadOnlyList<Guide> Guides { get; set; } = Array.Empty<Guide>();
	public float ReachPx { get; set; } = 8f;   // how near, on screen

	public bool On => (ToGrid && GridSize > 0) || (ToGuides && Guides.Count > 0);

	// The point, snapped. zoom: screen pixels per unit.
	public Vector2 Snap(Vector2 point, float zoom) => point + Offset(new[] { point }, zoom);

	// How far to move these points together so the one nearest a line lands on it, each way on its own
	public Vector2 Offset(IReadOnlyList<Vector2> points, float zoom)
	{
		if (!On || points.Count == 0 || zoom <= 0) return Vector2.Zero;
		float reach = ReachPx / zoom;
		return new Vector2(Nearest(points.Select(p => p.X), vertical: true, reach),
						   Nearest(points.Select(p => p.Y), vertical: false, reach));
	}

	// The smallest move (within reach) that puts one of the values on a line, or 0
	private float Nearest(IEnumerable<float> values, bool vertical, float reach)
	{
		float best = 0f, bestDistance = float.MaxValue;
		foreach (var value in values)
			foreach (var line in LinesNear(value, vertical))
			{
				float distance = MathF.Abs(line - value);
				if (distance <= reach && distance < bestDistance)
				{
					bestDistance = distance;
					best = line - value;
				}
			}
		return best;
	}

	// The grid line and the guides that could catch a value (vertical lines catch x, the rest y)
	private IEnumerable<float> LinesNear(float value, bool vertical)
	{
		if (ToGrid && GridSize > 0) yield return MathF.Round(value / GridSize) * GridSize;
		if (!ToGuides) yield break;
		foreach (var guide in Guides)
			if (guide.Vertical == vertical) yield return guide.At;
	}

	// The angle in steps of 15°, for Shift-turning
	public static float StepAngle(float radians) => MathF.Round(radians / (MathF.PI / 12)) * (MathF.PI / 12);
}
