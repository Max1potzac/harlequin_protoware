using System.Numerics;

namespace Animator.Core.Rigging;

public class Slot
{
    public string Name { get; set; } = "";
	public Bone Bone { get; set; }
	public string? ActiveAttachment { get; set; }   // this is what the attachment timeline will change

    public string? SetupAttachment { get; set; }
    public Slot(string name, Bone bone) { Name = name; Bone = bone; }

	// Placement keys (milestone 9.3): the part moved, turned and scaled on its bone, on top of
	// its drawing's own placement. Animated values, back to "nothing extra" every frame.
	public Vector2 Offset { get; set; }                   // in the bone's space
	public float Rotation { get; set; }                   // radians, around the pivot
	public Vector2 Scale { get; set; } = Vector2.One;     // multiplies, around the pivot

	public void ResetPlacement()
	{
		Offset = Vector2.Zero;
		Rotation = 0f;
		Scale = Vector2.One;
	}

	// Image pixels → the bone's space for one of this part's drawings, placement keys included
	public Matrix3x2 ImageToBone(Attachment drawing, int width, int height) =>
		drawing.LocalMatrix(width, height, Offset, Rotation, Scale);
}