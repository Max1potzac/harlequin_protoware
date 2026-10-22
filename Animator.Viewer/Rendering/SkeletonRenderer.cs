using System.Numerics;
using Animator.Core.Rigging;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

public class SkeletonRenderer
{
    private readonly AssetStore _assets;
    public bool ShowImages { get; set; } = true;
    public bool ShowBones { get; set; } = true;

    public float Zoom { get; set; } = 1f;
    public Bone? Selected { get; set; }
    public SkeletonRenderer(AssetStore assets) => _assets = assets;

    public void Draw(SKCanvas canvas, Skeleton skeleton)
    {
        DrawAxes(canvas);
        if (ShowImages) DrawSlots(canvas, skeleton);
        if (ShowBones) DrawBones(canvas, skeleton);
    }
    // Nearest = crisp pixels (pixel art). Linear = smooth when rotated/scaled.
    public bool PixelArt { get; set; }

    private SKSamplingOptions Sampling => PixelArt
        ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
        : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
    private void DrawAxes(SKCanvas canvas)
    {
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 40), StrokeWidth = 1f / Zoom };
        canvas.DrawLine(-100000, 0, 100000, 0, paint);
        canvas.DrawLine(0, -100000, 0, 100000, paint);
    }

    private void DrawBones(SKCanvas canvas, Skeleton skeleton)
    {
        float px = 1f / Zoom;

        using var line = new SKPaint
        {
            Color = new SKColor(255, 200, 60),
            StrokeWidth = 3 * px,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round
        };
        using var selLine = new SKPaint
        {
            Color = new SKColor(80, 220, 255),
            StrokeWidth = 4 * px,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round
        };
        using var idleLine = new SKPaint
        {
            Color = new SKColor(255, 200, 60, 60),
            StrokeWidth = 2 * px,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round
        };
        using var joint = new SKPaint { Color = new SKColor(255, 90, 90), IsAntialias = true };
        using var selJoint = new SKPaint { Color = new SKColor(80, 220, 255), IsAntialias = true };
        using var idleJoint = new SKPaint { Color = new SKColor(255, 90, 90, 60), IsAntialias = true };

        foreach (var bone in skeleton.AllBones())
        {
            bool sel = bone == Selected;
            // An IK target whose constraint is switched off (FK mode) is drawn faded
            bool idle = !sel && skeleton.IkConstraints.Exists(c => c.Target == bone && c.Mix <= 0f);

            var a = bone.World.Translation;
            var b = bone.TipWorld;
            canvas.DrawLine(a.X, a.Y, b.X, b.Y, sel ? selLine : idle ? idleLine : line);
            canvas.DrawCircle(a.X, a.Y, (sel ? 6 : 5) * px, sel ? selJoint : idle ? idleJoint : joint);
        }
    }

	// Slot list order = draw order (first drawn is at the back)
	private void DrawSlots(SKCanvas canvas, Skeleton skeleton)
	{
		using var outline = new SKPaint
		{
			Color = new SKColor(80, 220, 255),
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 2f / Zoom,
			IsAntialias = true
		};

		foreach (var slot in skeleton.Slots)
		{
			var att = skeleton.ResolveAttachment(slot);
			if (att == null || !skeleton.Assets.TryGetValue(att.AssetId, out var asset)) continue;
			var bmp = _assets.Get(asset);
			if (bmp == null) continue;

			var m = ToSk(ImageToWorld(slot, att, bmp.Width, bmp.Height));
			canvas.Save();
			canvas.Concat(in m);
			canvas.DrawBitmap(bmp, 0, 0, Sampling, null);
			if (asset == Highlight) canvas.DrawRect(0, 0, bmp.Width, bmp.Height, outline);
			canvas.Restore();
		}
	}
	// The asset open in the paint editor gets an outline
	public Asset? Highlight { get; set; }

	// Maps image pixels (y-down, origin top-left) into world space. Drawing and
	// picking both use this, so what you click is always exactly what you see.
	public static Matrix3x2 ImageToWorld(Slot slot, Attachment att, int width, int height) =>
		  Matrix3x2.CreateTranslation(-att.Pivot.X * width, -att.Pivot.Y * height)
		* Matrix3x2.CreateScale(1, -1)                     // image y-down -> world y-up
		* Matrix3x2.CreateScale(att.Scale)
		* Matrix3x2.CreateRotation(att.Rotation)
		* Matrix3x2.CreateTranslation(att.Offset)
		* slot.Bone.World;

	// Finds the topmost part under a world point, ignoring transparent pixels
	public (Slot Slot, Asset Asset)? Pick(Skeleton skeleton, Vector2 world)
	{
		for (int i = skeleton.Slots.Count - 1; i >= 0; i--)   // last drawn = on top
		{
			var slot = skeleton.Slots[i];
			var att = skeleton.ResolveAttachment(slot);
			if (att == null || !skeleton.Assets.TryGetValue(att.AssetId, out var asset)) continue;
			var img = _assets.Get(asset);
			if (img == null) continue;

			if (!Matrix3x2.Invert(ImageToWorld(slot, att, img.Width, img.Height), out var toImage)) continue;
			var p = Vector2.Transform(world, toImage);
			int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
			if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) continue;

			if (img.GetPixel(x, y).Alpha > 10) return (slot, asset);
		}
		return null;
	}

	// Matrix3x2 (row-vector) -> SKMatrix. Same numbers, different names.
	private static SKMatrix ToSk(Matrix3x2 m) => new()
    {
        ScaleX = m.M11,
        SkewX = m.M21,
        TransX = m.M31,
        SkewY = m.M12,
        ScaleY = m.M22,
        TransY = m.M32,
        Persp0 = 0,
        Persp1 = 0,
        Persp2 = 1
    };
}