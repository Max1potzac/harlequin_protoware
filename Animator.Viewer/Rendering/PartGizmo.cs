using System.Numerics;

namespace Animator.Viewer.Rendering;

// Handle positions, in world space
public sealed record PartHandles(Vector2[] Corners, Vector2 Top, Vector2 Rotate, Vector2 Pivot);

public static class PartGizmo
{
	private const float RotateHandleDistancePx = 28f;   // stays the same size on screen at any zoom

	public static PartHandles? For(Skeleton skeleton, Slot slot, float zoom)
	{
		var att = skeleton.ResolveAttachment(slot);
		if (att == null || !skeleton.Assets.TryGetValue(att.AssetId, out var asset)) return null;

		int w = asset.Width, h = asset.Height;
		var m = slot.ImageToBone(att, w, h) * slot.Bone.World;
		Vector2 P(float x, float y) => Vector2.Transform(new Vector2(x, y), m);

		var corners = new[] { P(0, 0), P(w, 0), P(w, h), P(0, h) };
		var center = P(w / 2f, h / 2f);
		var top = P(w / 2f, 0);   // top of the image (images are y-down)

		var dir = top - center;
		dir = dir.LengthSquared() > 1e-6f ? Vector2.Normalize(dir) : Vector2.UnitY;

		return new PartHandles(corners, top, top + dir * (RotateHandleDistancePx / zoom),
							   P(att.Pivot.X * w, att.Pivot.Y * h));
	}
}