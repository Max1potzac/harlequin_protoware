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

	// Inside an object in the editor: locked parts (the lock in the rig panel) can't be picked,
	// so a click goes through to what's behind them
	public bool SkipLocked { get; set; }

	// Colour layers (an object's, and those inside drawings) shown. Exports turn them off unless
	// asked to bake them in.
	public bool ColorLayers { get; set; } = true;

	// A part outlined, as one the mouse is over in a list (the Colour view's Leave out)
	public Slot? HoverSlot { get; set; }

	// A faint dashed edge round each clipping mask's picture (while editing; never in exports)
	public bool MaskOutlines { get; set; } = true;
	private bool Pickable(Slot slot) => Shown(slot) && !(SkipLocked && slot.Locked);

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
		using var path = ShapePaths.Polyline(box, closed: true);
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

            // Its own colour (the usual yellow, or the one picked for it); faded while locked
            var paint = sel ? selLine : bone.SpaceOverridden ? spaceLine : idle ? idleLine : line;
            if (!sel && !bone.SpaceOverridden)
                paint.Color = BoneColor(bone).WithAlpha(idle ? (byte)60 : bone.Locked ? (byte)110 : (byte)255);
            joint.Color = new SKColor(255, 90, 90, bone.Locked ? (byte)110 : (byte)255);
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

		using var maskEdge = new SKPaint
		{
			Color = new SKColor(200, 140, 255, 150),
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 1f / Zoom,
			IsAntialias = true,
			PathEffect = dash
		};

		// The object's colour layers: one filter for each set of them a part gets (most get all)
		var looks = ColorLayers && skeleton.ColorLayers.Any(skeleton.Applies) ? new Dictionary<long, SKColorFilter?>() : null;
		void DrawOne(Slot slot)
		{
			if (!Shown(slot) || Picture(skeleton, slot) is not { } p) return;
			var look = looks != null ? LookFor(skeleton, slot, looks) : null;
			if (MeshOf(p.Drawing, p.Bitmap) is { } mesh)
			{
				DrawMeshPart(canvas, slot, p.Drawing, mesh, p.Bitmap, look);
				if (p.Asset == Highlight || slot == HoverSlot) DrawMeshEdges(canvas, slot, p.Drawing, mesh, outline);
				return;
			}
			canvas.Save();
			canvas.Concat(in p.ToWorld);
			DrawPart(canvas, slot, p.Bitmap, look);
			if (p.Asset == Highlight || slot == HoverSlot) canvas.DrawRect(0, 0, p.Bitmap.Width, p.Bitmap.Height, outline);
			else if (PartsOf != null && slot.Bone == PartsOf) canvas.DrawRect(0, 0, p.Bitmap.Width, p.Bitmap.Height, onBone);
			canvas.Restore();
		}

		try
		{
			var order = skeleton.DrawOrder;   // as it stands now (an animation can key it)
			for (int i = 0; i < order.Count; i++)
			{
				var slot = order[i];
				if (!slot.IsMask)
				{
					DrawOne(slot);
					continue;
				}
				// A clipping mask: the parts it clips, drawn on their own, then cut to its picture.
				// Hidden (its eye) or showing nothing: it clips nothing.
				var clipped = MaskOps.Clipped(order, slot);
				var picture = Shown(slot) ? Picture(skeleton, slot) : null;
				if (picture is not { } mask)
					continue;   // (the parts it would clip are drawn as they come)
				if (slot.MaskShows) DrawOne(slot);
				canvas.SaveLayer();
				foreach (var part in clipped) DrawOne(part);   // (a mask among them: drawn as a part)
				using (var cut = new SKPaint { BlendMode = SKBlendMode.DstIn })
				{
					canvas.SaveLayer(cut);
					if (MeshOf(mask.Drawing, mask.Bitmap) is { } maskMesh)
					{
						using var plain = new SKPaint();
						DrawMesh(canvas, slot, mask.Drawing, maskMesh, mask.Bitmap, plain);
					}
					else
					{
						canvas.Save();
						canvas.Concat(in mask.ToWorld);
						canvas.DrawBitmap(mask.Bitmap, 0, 0, Sampling, null);
						canvas.Restore();
					}
					canvas.Restore();   // keeps the clipped parts only where the mask has pixels
				}
				canvas.Restore();
				if (MaskOutlines)
				{
					canvas.Save();
					canvas.Concat(in mask.ToWorld);
					canvas.DrawRect(0, 0, mask.Bitmap.Width, mask.Bitmap.Height, maskEdge);
					canvas.Restore();
				}
				i += clipped.Count;
			}
		}
		finally
		{
			if (looks != null)
				foreach (var filter in looks.Values) filter?.Dispose();
		}
	}

	// A part's picture as it shows now, and its pixels → world (null: it shows nothing)
	private (Attachment Drawing, Asset Asset, SKBitmap Bitmap, SKMatrix ToWorld)? Picture(Skeleton skeleton, Slot slot)
	{
		var att = skeleton.ResolveAttachment(slot);
		if (att == null || !skeleton.Assets.TryGetValue(att.AssetId, out var asset)) return null;
		var bmp = _assets.Get(asset, ColorLayers);
		if (bmp == null) return null;
		return (att, asset, bmp, ToSk(ImageToWorld(slot, att, bmp.Width, bmp.Height)));
	}

	// The drawing's mesh, when it fits its picture (one made before the canvas was resized: drawn flat)
	private static Mesh? MeshOf(Attachment drawing, SKBitmap bitmap) =>
		drawing.Mesh is { } mesh && mesh.Width == bitmap.Width && mesh.Height == bitmap.Height && mesh.Triangles.Length > 0 ? mesh : null;

	// A meshed part: its picture spread over its triangles where its points are now, with its blend
	// mode, tint and opacity (look: the object's colour layers for it, not disposed here)
	private void DrawMeshPart(SKCanvas canvas, Slot slot, Attachment drawing, Mesh mesh, SKBitmap bmp, SKColorFilter? look)
	{
		bool tinted = slot.Color != Vector3.One;
		float opacity = Math.Clamp(slot.Opacity, 0f, 1f);
		using var tint = tinted ? TintFilter(slot.Color) : null;
		using var both = tint != null && look != null ? SKColorFilter.CreateCompose(look, tint) : null;
		using var paint = new SKPaint
		{
			BlendMode = ToSk(slot.Blend),
			Color = SKColors.White.WithAlpha((byte)MathF.Round(opacity * 255)),
			ColorFilter = both ?? tint ?? look
		};
		DrawMesh(canvas, slot, drawing, mesh, bmp, paint);
	}

	private void DrawMesh(SKCanvas canvas, Slot slot, Attachment drawing, Mesh mesh, SKBitmap bmp, SKPaint paint)
	{
		var posed = MeshOps.Posed(slot, drawing, mesh);
		var t = mesh.Triangles;
		var positions = new SKPoint[t.Length];
		var texs = new SKPoint[t.Length];
		for (int i = 0; i < t.Length; i++)
		{
			positions[i] = new SKPoint(posed[t[i]].X, posed[t[i]].Y);
			texs[i] = new SKPoint(mesh.Points[t[i]].X, mesh.Points[t[i]].Y);
		}
		using var image = SKImage.FromBitmap(bmp);
		using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling);
		paint.Shader = shader;
		canvas.DrawVertices(SKVertexMode.Triangles, positions, texs, null, paint);
		paint.Shader = null;
	}

	// A meshed part's triangles, outlined where they are now
	private static void DrawMeshEdges(SKCanvas canvas, Slot slot, Attachment drawing, Mesh mesh, SKPaint paint)
	{
		var posed = MeshOps.Posed(slot, drawing, mesh);
		var t = mesh.Triangles;
		for (int i = 0; i < t.Length; i += 3)
		{
			using var path = ShapePaths.Polyline(new[] { posed[t[i]], posed[t[i + 1]], posed[t[i + 2]] }, closed: true);
			canvas.DrawPath(path, paint);
		}
	}

	// The colour layers that change this part, as one filter (first layer first)
	private static SKColorFilter? LookFor(Skeleton skeleton, Slot slot, Dictionary<long, SKColorFilter?> made)
	{
		var layers = skeleton.ColorLayers;
		long which = 0;
		for (int i = 0; i < layers.Count && i < 63; i++)
			if (layers[i].Changes(slot) && skeleton.Applies(layers[i]) && layers[i].Adjust.Kind != ColorAdjustKind.ReplaceColor) which |= 1L << i;
		if (which == 0) return null;
		if (made.TryGetValue(which, out var known)) return known;
		SKColorFilter? filter = null;
		for (int i = 0; i < layers.Count && i < 63; i++)
			if ((which & (1L << i)) != 0) filter = ColorFilters.Then(filter, ColorFilters.For(layers[i].Adjust, 1f));
		made[which] = filter;
		return filter;
	}

	// One part's pixels, with its blend mode, tint and opacity (plainly when it has none)
	// look: the object's colour layers for it, after its own tint (not disposed here)
	private void DrawPart(SKCanvas canvas, Slot slot, SKBitmap bmp, SKColorFilter? look = null)
	{
		bool tinted = slot.Color != Vector3.One;
		float opacity = Math.Clamp(slot.Opacity, 0f, 1f);
		if (slot.Blend == BlendMode.Normal && !tinted && opacity >= 1f && look == null)
		{
			canvas.DrawBitmap(bmp, 0, 0, Sampling, null);
			return;
		}

		using var tint = tinted ? TintFilter(slot.Color) : null;
		using var both = tint != null && look != null ? SKColorFilter.CreateCompose(look, tint) : null;
		using var paint = new SKPaint
		{
			BlendMode = ToSk(slot.Blend),
			Color = SKColors.White.WithAlpha((byte)MathF.Round(opacity * 255)),
			ColorFilter = both ?? tint ?? look
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
			if (!Pickable(slot) || (slot.IsMask && !slot.MaskShows)) continue;   // (a mask that isn't drawn: pick it in the layers)
			if (OnPixels(skeleton, slot, world) is not { } asset) continue;
			// Clipped: only where its mask shows it
			if (MaskOps.MaskOf(skeleton, slot) is { } mask && Shown(mask) && skeleton.ResolveAttachment(mask) != null
				&& OnPixels(skeleton, mask, world) == null) continue;
			return (slot, asset);
		}
		return null;
	}

	// The part's pixels under a world point show (its pixels' asset), or null
	private Asset? OnPixels(Skeleton skeleton, Slot slot, Vector2 world)
	{
		var att = skeleton.ResolveAttachment(slot);
		if (att == null || !skeleton.Assets.TryGetValue(att.AssetId, out var asset)) return null;
		var img = _assets.Get(asset);
		if (img == null) return null;
		Vector2 p;
		if (MeshOf(att, img) is { } mesh)
		{
			if (MeshOps.PixelAt(mesh, MeshOps.Posed(slot, att, mesh), world) is not { } pixel) return null;
			p = pixel;
		}
		else if (Matrix3x2.Invert(ImageToWorld(slot, att, img.Width, img.Height), out var toImage)) p = Vector2.Transform(world, toImage);
		else return null;
		int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
		if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) return null;
		return img.GetPixel(x, y).Alpha > 10 ? asset : null;
	}
	// Inside any part's rectangle, see-through pixels included (a blank drawing can be grabbed too)
	public bool InsidePartBox(Skeleton skeleton, Vector2 world)
	{
		foreach (var slot in skeleton.Slots)
		{
			if (!Pickable(slot)) continue;
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
		var hover = HoverSlot;
		bool maskEdges = MaskOutlines;
		Highlight = null;   // just the pictures
		PartsOf = null;
		HoverSlot = null;
		MaskOutlines = false;
		DrawSlots(canvas, skeleton);
		Highlight = highlight;
		PartsOf = partsOf;
		HoverSlot = hover;
		MaskOutlines = maskEdges;
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
			var points = att.Mesh is { } mesh && mesh.Width == asset.Width && mesh.Height == asset.Height && mesh.Points.Length > 0
				? MeshOps.Posed(slot, att, mesh)   // (a mesh can bend past its picture's box)
				: new[] { new Vector2(0, 0), new Vector2(asset.Width, 0), new Vector2(asset.Width, asset.Height), new Vector2(0, asset.Height) }
					.Select(corner => Vector2.Transform(corner, m)).ToArray();
			foreach (var p in points)
			{
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
	// A bone's line colour: its own, or the usual yellow
	public static readonly SKColor DefaultBoneColor = new(255, 200, 60);
	public static SKColor BoneColor(Bone bone) => bone.EditorColor is { } c
		? new SKColor((byte)Math.Clamp(MathF.Round(c.X * 255), 0, 255), (byte)Math.Clamp(MathF.Round(c.Y * 255), 0, 255), (byte)Math.Clamp(MathF.Round(c.Z * 255), 0, 255))
		: DefaultBoneColor;

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
			Color = slot.Locked ? new SKColor(160, 160, 160, 200) : new SKColor(255, 170, 60),   // locked: just its box, in grey
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
		if (!ShowPartHandles || slot.Locked) return;

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