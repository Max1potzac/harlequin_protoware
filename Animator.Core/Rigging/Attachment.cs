using System.Numerics;

namespace Animator.Core.Rigging;

public class Attachment
{
    public string Name { get; set; } = "";
    public Guid AssetId { get; set; }

    // Placement relative to the slot's bone
    public Vector2 Offset { get; set; }
    public float Rotation { get; set; }
    public Vector2 Scale { get; set; } = Vector2.One;

    // Normalized pivot: (0.5, 0.5) = center, (0,0) = top-left.
    // Because it's normalized, resizing the image keeps the anchor sensible.
    public Vector2 Pivot { get; set; } = new(0.5f, 0.5f);
	// Image pixels (y-down, origin top-left) → the bone's space.
	// Rendering, picking and the transform box all use this one definition.
	public Matrix3x2 LocalMatrix(int width, int height) =>
		  Matrix3x2.CreateTranslation(-Pivot.X * width, -Pivot.Y * height)
		* Matrix3x2.CreateScale(1, -1)
		* Matrix3x2.CreateScale(Scale)
		* Matrix3x2.CreateRotation(Rotation)
		* Matrix3x2.CreateTranslation(Offset);
}