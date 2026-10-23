using System.Numerics;

namespace Animator.Core.Rigging;

public enum LookShape { Ellipse, Rectangle }

// Moves a bone (an iris, a pupil, a highlight...) toward a target bone, but never
// outside a shape around its rest position. Works in the parent's space, so the
// boundary turns and scales with the head.
public class LookConstraint
{
	public string Name { get; set; } = "";
	public Bone Bone { get; set; } = null!;      // the part that moves (the iris)
	public Bone Target { get; set; } = null!;    // what it looks at (usually a free root bone)
	public LookShape Shape { get; set; } = LookShape.Ellipse;
	public Vector2 Radius { get; set; } = new(10, 6);   // half-width and half-height of the shape
	public float Follow { get; set; } = 0.25f;   // how far it moves per unit of distance to the target
	public float SetupMix { get; set; } = 1f;
	public float Mix { get; set; } = 1f;

	// Rest position (in the parent's space) from the last solve; the editor draws the shape around it
	public Vector2 Center { get; private set; }

	public void Solve()
	{
		Center = Bone.Position;   // after animation and limits, before this constraint
		if (Mix <= 0f) return;

		// Where's the target, seen from the iris's parent (or whatever it follows right now)?
		Vector2 target = Target.World.Translation;
		if (Bone.SpaceParent is { } space)
		{
			if (!Matrix3x2.Invert(space.World, out var inverse)) return;   // parent squashed flat
			target = Vector2.Transform(target, inverse);
		}

		var offset = Clamp((target - Center) * Follow);
		Bone.Position = Center + offset * Mix;
		Bone.UpdateWorld();   // refresh the iris and anything attached to it
	}

	private Vector2 Clamp(Vector2 v)
	{
		float rx = MathF.Max(Radius.X, 0.001f);
		float ry = MathF.Max(Radius.Y, 0.001f);

		if (Shape == LookShape.Rectangle)
			return new Vector2(Math.Clamp(v.X, -rx, rx), Math.Clamp(v.Y, -ry, ry));

		// Ellipse: if outside, pull back along the same direction onto the edge
		float d = v.X * v.X / (rx * rx) + v.Y * v.Y / (ry * ry);
		return d <= 1f ? v : v / MathF.Sqrt(d);
	}
}