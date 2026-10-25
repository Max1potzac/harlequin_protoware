using System.Numerics;
using Animator.Core.Rigging;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

public class SkeletonRenderer
{
    private readonly AssetStore _assets;
    public bool ShowImages { get; set; } = true;
    public bool ShowBones { get; set; } = true;
	public (Vector2 A, Vector2 B)? GhostBone { get; set; }
	public Bone? TipHandleOf { get; set; }   // a ring at this bone's end: drag it to say where the bone ends
	public float Zoom { get; set; } = 1f;
    public Bone? Selected { get; set; }
    public SkeletonRenderer(AssetStore assets) => _assets = assets;
	public Slot? SelectedSlot { get; set; }
	public bool ShowPartHandles { get; set; } = true;
	public bool ShowPivotHandle { get; set; } = true;   // Setup mode only: the pivot is part of the rest setup

	// Everything picked (the selected bone or part among them): drawn as picked too
	public IReadOnlySet<Bone> PickedBones { get; set; } = new HashSet<Bone>();
	public IReadOnlySet<Slot> PickedSlots { get; set; } = new HashSet<Slot>();

	// A box being dragged over things to pick them (its corners in the world)
	public IReadOnlyList<Vector2>? SelectionBox { get; set; }

	// Inside an object in the editor: hidden bones and parts (the eye in the rig panel) aren't
	// drawn or picked. Exports and thumbnails draw everything.
	public bool HideHidden { get; set; }
	private bool Shown(Slot slot) => !HideHidden || !slot.IsHidden;
	private bool Shown(Bone bone) => !HideHidden || !bone.IsHidden;

	public void Draw(SKCanvas canvas, Skeleton skeleton)
	{
		DrawAxes(canvas);
		DrawParts(canvas, skeleton);
		DrawOverlays(canvas, skeleton);
	}

	// The pieces of Draw, for scenes: every object's parts first, then the edited object's
	// bones and handles on top of everything
	public void DrawParts(SKCanvas canvas, Skeleton skeleton)
	{
		if (ShowImages) DrawSlots(canvas, skeleton);
	}

	public void DrawOverlays(SKCanvas canvas, Skeleton skeleton)
	{
		if (ShowBones)
		{
			DrawLookGuides(canvas, skeleton);
			DrawBones(canvas, skeleton);
		}
		DrawPartGizmo(canvas, skeleton);
		DrawSelectionBox(canvas);
	}

	// The box being dragged to pick things: see-through, with a dashed edge
	private void DrawSelectionBox(SKCanvas canvas)
	{
		if (SelectionBox is not { Count: 4 } box) return;
		using var path = new SKPath();
		path.MoveTo(box[0].X, box[0].Y);
		for (int i = 1; i < 4; i++) path.LineTo(box[i].X, box[i].Y);
		path.Close();
		using var fill = new SKPaint { Color = new SKColor(80, 220, 255, 32), Style = SKPaintStyle.Fill, IsAntialias = true };
		using var dash = SKPathEffect.CreateDash(new[] { 5f / Zoom, 4f / Zoom }, 0);
		using var edge = new SKPaint
		{
			Color = new SKColor(80, 220, 255, 220),
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 1f / Zoom,
			IsAntialias = true,
			PathEffect = dash
		};
		canvas.DrawPath(path, fill);
		canvas.DrawPath(path, edge);
	}

	// At scene level: faint lines for the bones of an object with no pictures yet, so it can
	// still be seen and grabbed
	public void DrawBoneHints(SKCanvas canvas, Skeleton skeleton)
	{
		using var line = new SKPaint
		{
			Color = new SKColor(255, 200, 60, 110),
			StrokeWidth = 2f / Zoom,
			IsAntialias = true,
			Style = SKPaintStyle.Stroke,
			StrokeCap = SKStrokeCap.Round
		};
		foreach (var bone in skeleton.AllBones())
		{
			if (!Shown(bone)) continue;
			var a = bone.World.Translation;
			var b = bone.TipWorld;
			canvas.DrawLine(a.X, a.Y, b.X, b.Y, line);
		}
	}

	// Another object while you edit one: see-through, as one layer
	public void DrawFaded(SKCanvas canvas, Skeleton skeleton, float opacity)
	{
		if (!ShowImages || ImageBounds(skeleton) is not { } bounds) return;
		using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(Math.Clamp(opacity, 0f, 1f) * 255)) };
		canvas.SaveLayer(bounds, paint);
		DrawImages(canvas, skeleton);
		canvas.Restore();
	}
	// Nearest = crisp pixels (pixel art). Linear = smooth when rotated/scaled.
	public bool PixelArt { get; set; }

    private SKSamplingOptions Sampling => PixelArt
        ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
        : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
    public void DrawAxes(SKCanvas canvas)
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

        // Detached or held bones (space keys) are purple; a held bone gets a dashed line to its holder
        using var spaceLine = new SKPaint
        {
            Color = new SKColor(190, 130, 255),
            StrokeWidth = 3 * px,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round
        };
        using var holderDash = SKPathEffect.CreateDash(new[] { 5 * px, 4 * px }, 0);
        using var holderLine = new SKPaint
        {
            Color = new SKColor(190, 130, 255, 150),
            StrokeWidth = 1.5f * px,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            PathEffect = holderDash
        };

        foreach (var bone in skeleton.AllBones())
        {
            if (!Shown(bone)) continue;
            bool primary = bone == Selected;
            bool sel = primary || PickedBones.Contains(bone);
            // An IK target whose constraint is switched off (FK mode) is drawn faded
            bool idle = !sel && skeleton.IkConstraints.Exists(c => c.Target == bone && c.Mix <= 0f);

            var a = bone.World.Translation;
            var b = bone.TipWorld;

            if (bone.SpaceOverridden && bone.SpaceParent is { } holder)
            {
                var h = holder.World.Translation;
                canvas.DrawLine(a.X, a.Y, h.X, h.Y, holderLine);
            }

            var paint = sel ? selLine : bone.SpaceOverridden ? spaceLine : idle ? idleLine : line;
            canvas.DrawLine(a.X, a.Y, b.X, b.Y, paint);
            canvas.DrawCircle(a.X, a.Y, (primary ? 6 : 5) * px, sel ? selJoint : idle ? idleJoint : joint);
        }
		if (TipHandleOf is { } ended)
		{
			using var ring = new SKPaint { Color = new SKColor(80, 220, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 2 * px, IsAntialias = true };
			using var hole = new SKPaint { Color = new SKColor(20, 24, 30, 200), IsAntialias = true };
			var t = ended.TipWorld;
			canvas.DrawCircle(t.X, t.Y, 5 * px, hole);
			canvas.DrawCircle(t.X, t.Y, 5 * px, ring);
		}
		if (GhostBone is { } g)
		{
			using var ghost = new SKPaint
			{
				Color = new SKColor(80, 220, 255, 170),
				StrokeWidth = 3 * px,
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeCap = SKStrokeCap.Round
			};
			canvas.DrawLine(g.A.X, g.A.Y, g.B.X, g.B.Y, ghost);
			canvas.DrawCircle(g.A.X, g.A.Y, 5 * px, selJoint);
		}
	}

	// The parts on this bone get a dashed outline (the selected bone: what's on it)
	public Bone? PartsOf { get; set; }

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
		using var dash = SKPathEffect.CreateDash(new[] { 6f / Zoom, 4f / Zoom }, 0);
		using var onBone = new SKPaint
		{
			Color = new SKColor(255, 170, 60, 220),
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 1.5f / Zoom,
			IsAntialias = true,
			PathEffect = dash
		};

		foreach (var slot in skeleton.DrawOrder)   // as it stands now (an animation can key it)
		{
			if (!Shown(slot)) continue;
			var att = skeleton.ResolveAttachment(slot);
			if (att == null || !skeleton.Assets.TryGetValue(att.AssetId, out var asset)) continue;
			var bmp = _assets.Get(asset);
			if (bmp == null) continue;

			var m = ToSk(ImageToWorld(slot, att, bmp.Width, bmp.Height));
			canvas.Save();
			canvas.Concat(in m);
			DrawPart(canvas, slot, bmp);
			if (asset == Highlight) canvas.DrawRect(0, 0, bmp.Width, bmp.Height, outline);
			else if (PartsOf != null && slot.Bone == PartsOf) canvas.DrawRect(0, 0, bmp.Width, bmp.Height, onBone);
			canvas.Restore();
		}
	}

	// One part's pixels, with its blend mode, tint and opacity (plainly when it has none)
	private void DrawPart(SKCanvas canvas, Slot slot, SKBitmap bmp)
	{
		bool tinted = slot.Color != Vector3.One;
		float opacity = Math.Clamp(slot.Opacity, 0f, 1f);
		if (slot.Blend == BlendMode.Normal && !tinted && opacity >= 1f)
		{
			canvas.DrawBitmap(bmp, 0, 0, Sampling, null);
			return;
		}

		using var tint = tinted ? TintFilter(slot.Color) : null;
		using var paint = new SKPaint
		{
			BlendMode = ToSk(slot.Blend),
			Color = SKColors.White.WithAlpha((byte)MathF.Round(opacity * 255)),
			ColorFilter = tint
		};
		canvas.DrawBitmap(bmp, 0, 0, Sampling, paint);
	}

	public static SKBlendMode ToSk(BlendMode blend) => blend switch
	{
		BlendMode.Add => SKBlendMode.Plus,
		BlendMode.Multiply => SKBlendMode.Multiply,
		BlendMode.Screen => SKBlendMode.Screen,
		_ => SKBlendMode.SrcOver
	};

	// Multiplies red, green and blue by the tint (white leaves the drawing as it is)
	public static SKColorFilter TintFilter(Vector3 color) => SKColorFilter.CreateColorMatrix(new[]
	{
		color.X, 0, 0, 0, 0,
		0, color.Y, 0, 0, 0,
		0, 0, color.Z, 0, 0,
		0, 0, 0, 1, 0
	});
	// The asset open in the paint editor gets an outline
	public Asset? Highlight { get; set; }

	// Maps image pixels (y-down, origin top-left) into world space. Drawing and
	// picking both use this, so what you click is always exactly what you see.
	public static Matrix3x2 ImageToWorld(Slot slot, Attachment att, int width, int height) =>
		slot.ImageToBone(att, width, height) * slot.Bone.World;

	// Finds the topmost part under a world point, ignoring transparent pixels
	public (Slot Slot, Asset Asset)? Pick(Skeleton skeleton, Vector2 world)
	{
		var order = skeleton.DrawOrder;
		for (int i = order.Count - 1; i >= 0; i--)   // last drawn = on top
		{
			var slot = order[i];
			if (!Shown(slot)) continue;
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
	// Inside any part's rectangle, see-through pixels included (a blank drawing can be grabbed too)
	public bool InsidePartBox(Skeleton skeleton, Vector2 world)
	{
		foreach (var slot in skeleton.Slots)
		{
			if (!Shown(slot)) continue;
			var att = skeleton.ResolveAttachment(slot);
			if (att == null || !skeleton.Assets.TryGetValue(att.AssetId, out var asset)) continue;
			if (!Matrix3x2.Invert(ImageToWorld(slot, att, asset.Width, asset.Height), out var toImage)) continue;
			var p = Vector2.Transform(world, toImage);
			if (p.X >= 0 && p.Y >= 0 && p.X <= asset.Width && p.Y <= asset.Height) return true;
		}
		return false;
	}

	public void DrawImages(SKCanvas canvas, Skeleton skeleton)
	{
		var highlight = Highlight;
		var partsOf = PartsOf;
		Highlight = null;   // just the pictures
		PartsOf = null;
		DrawSlots(canvas, skeleton);
		Highlight = highlight;
		PartsOf = partsOf;
	}

	// One onion-skin ghost: the parts in the skeleton's current pose, tinted and see-through.
	// Drawn as one layer, so overlapping parts don't show through each other.
	public void DrawGhost(SKCanvas canvas, Skeleton skeleton, bool before, float opacity)
	{
		if (!ShowImages || ImageBounds(skeleton) is not { } bounds) return;

		using var tint = OnionSkin.TintFilter(before);
		using var paint = new SKPaint { Color = OnionSkin.Fade(opacity), ColorFilter = tint };
		canvas.SaveLayer(bounds, paint);
		DrawImages(canvas, skeleton);
		canvas.Restore();
	}

	// The world-space box around every visible part (null = nothing to draw). The margin leaves
	// room for smoothing at the edges (exports use none: they round out to whole pixels).
	public SKRect? ImageBounds(Skeleton skeleton, float margin = 2)
	{
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		foreach (var slot in skeleton.Slots)
		{
			var att = skeleton.ResolveAttachment(slot);
			if (att == null || !skeleton.Assets.TryGetValue(att.AssetId, out var asset)) continue;
			var m = ImageToWorld(slot, att, asset.Width, asset.Height);
			foreach (var corner in new[] { new Vector2(0, 0), new Vector2(asset.Width, 0),
										   new Vector2(asset.Width, asset.Height), new Vector2(0, asset.Height) })
			{
				var p = Vector2.Transform(corner, m);
				minX = MathF.Min(minX, p.X);
				minY = MathF.Min(minY, p.Y);
				maxX = MathF.Max(maxX, p.X);
				maxY = MathF.Max(maxY, p.Y);
			}
		}
		if (minX > maxX) return null;
		var rect = new SKRect(minX, minY, maxX, maxY);
		rect.Inflate(margin, margin);
		return rect;
	}

	// Matrix3x2 (row-vector) -> SKMatrix. Same numbers, different names.
	internal static SKMatrix ToSk(Matrix3x2 m) => new()
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

	// Each look constraint's boundary, drawn in the iris's parent space so it turns
	// with the head. Bright, plus a line to the target, when the iris or target is selected.
	private void DrawLookGuides(SKCanvas canvas, Skeleton skeleton)
	{
		float px = 1f / Zoom;
		foreach (var c in skeleton.LookConstraints)
		{
			bool active = c.Bone == Selected || c.Target == Selected;
			using var edge = new SKPaint
			{
				Color = active ? new SKColor(80, 220, 255) : new SKColor(255, 255, 255, 70),
				Style = SKPaintStyle.Stroke,
				StrokeWidth = (active ? 1.5f : 1f) * px,
				IsAntialias = true
			};

			canvas.Save();
			if (c.Bone.SpaceParent is { } parent)
			{
				var m = ToSk(parent.World);
				canvas.Concat(in m);
			}
			float rx = c.Radius.X, ry = c.Radius.Y;
			if (c.Shape == LookShape.Ellipse) canvas.DrawOval(c.Center.X, c.Center.Y, rx, ry, edge);
			else canvas.DrawRect(c.Center.X - rx, c.Center.Y - ry, rx * 2, ry * 2, edge);
			canvas.Restore();

			if (active)
			{
				using var dash = SKPathEffect.CreateDash(new[] { 4 * px, 4 * px }, 0);
				using var line = new SKPaint
				{
					Color = new SKColor(80, 220, 255, 120),
					StrokeWidth = px,
					IsAntialias = true,
					PathEffect = dash
				};
				var a = c.Bone.World.Translation;
				var b = c.Target.World.Translation;
				canvas.DrawLine(a.X, a.Y, b.X, b.Y, line);
			}
		}
	}
	// The other picked parts: their boxes, without handles
	private void DrawPickedParts(SKCanvas canvas, Skeleton skeleton)
	{
		if (PickedSlots.Count == 0) return;
		using var line = new SKPaint
		{
			Color = new SKColor(255, 170, 60, 200),
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 1.5f / Zoom,
			IsAntialias = true
		};
		foreach (var slot in PickedSlots)
		{
			if (slot == SelectedSlot || !Shown(slot) || PartGizmo.For(skeleton, slot, Zoom) is not { } h) continue;
			for (int i = 0; i < 4; i++)
			{
				var a = h.Corners[i];
				var b = h.Corners[(i + 1) % 4];
				canvas.DrawLine(a.X, a.Y, b.X, b.Y, line);
			}
		}
	}

	// Orange box around the selected part, with corner and rotate handles (and the pivot in Setup mode)
	private void DrawPartGizmo(SKCanvas canvas, Skeleton skeleton)
	{
		DrawPickedParts(canvas, skeleton);
		if (SelectedSlot is not { } slot || !Shown(slot) || PartGizmo.For(skeleton, slot, Zoom) is not { } h) return;
		float px = 1f / Zoom;

		using var line = new SKPaint
		{
			Color = new SKColor(255, 170, 60),
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 1.5f * px,
			IsAntialias = true
		};
		for (int i = 0; i < 4; i++)
		{
			var a = h.Corners[i];
			var b = h.Corners[(i + 1) % 4];
			canvas.DrawLine(a.X, a.Y, b.X, b.Y, line);
		}
		if (!ShowPartHandles) return;

		using var fill = new SKPaint { Color = new SKColor(255, 170, 60), IsAntialias = true };
		float s = 4 * px;
		foreach (var c in h.Corners) canvas.DrawRect(c.X - s, c.Y - s, s * 2, s * 2, fill);

		canvas.DrawLine(h.Top.X, h.Top.Y, h.Rotate.X, h.Rotate.Y, line);
		canvas.DrawCircle(h.Rotate.X, h.Rotate.Y, 5 * px, fill);
		if (!ShowPivotHandle) return;

		using var pivot = new SKPaint
		{
			Color = SKColors.White,
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 1.5f * px,
			IsAntialias = true
		};
		canvas.DrawCircle(h.Pivot.X, h.Pivot.Y, 6 * px, pivot);
		canvas.DrawLine(h.Pivot.X - 9 * px, h.Pivot.Y, h.Pivot.X + 9 * px, h.Pivot.Y, pivot);
		canvas.DrawLine(h.Pivot.X, h.Pivot.Y - 9 * px, h.Pivot.X, h.Pivot.Y + 9 * px, pivot);
	}
}