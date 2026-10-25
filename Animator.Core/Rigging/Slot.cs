using System.Numerics;

namespace Animator.Core.Rigging;

public class Slot
{
    public string Name { get; set; } = "";
	public Bone Bone { get; set; }
	public string? ActiveAttachment { get; set; }   // this is what the attachment timeline will change

    public string? SetupAttachment { get; set; }

	// Hidden while editing (the eye in the rig panel), or on a hidden bone: not drawn in the
	// viewport or picked there. Never in exports; saved with the project.
	public bool Hidden { get; set; }
	public bool IsHidden => Hidden || Bone.IsHidden;

	// Locked while editing: can't be picked or dragged in the viewport (clicks go through to
	// what's behind it); the rig panel can still pick it. Saved with the project.
	public bool Locked { get; set; }
    public Slot(string name, Bone bone) { Name = name; Bone = bone; }

	// Placement keys (milestone 9.3): the part moved, turned and scaled on its bone, on top of
	// its drawing's own placement. Animated values, back to "nothing extra" every frame.
	public Vector2 Offset { get; set; }                   // in the bone's space
	public float Rotation { get; set; }                   // radians, around the pivot
	public Vector2 Scale { get; set; } = Vector2.One;     // multiplies, around the pivot

	// Effects (milestone 9.4): how the part blends with what's behind it (a rest setting),
	// and a tint and opacity, which colour keys can animate. White and 1 = drawn as is.
	public BlendMode Blend { get; set; } = BlendMode.Normal;
	public Vector3 SetupColor { get; set; } = Vector3.One;   // multiplies red, green, blue (0..1 each)
	public float SetupOpacity { get; set; } = 1f;
	public Vector3 Color { get; set; } = Vector3.One;        // animated, back to the rest values every frame
	public float Opacity { get; set; } = 1f;

	// Everything animated back to rest: drawing, placement, colour
	public void ResetToSetup()
	{
		ActiveAttachment = SetupAttachment;
		ResetPlacement();
		Color = SetupColor;
		Opacity = SetupOpacity;
	}

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