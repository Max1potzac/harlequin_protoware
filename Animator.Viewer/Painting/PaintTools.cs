using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Painting;

// Base class: tools receive positions in image pixel coordinates
public abstract class PaintTool
{
	public abstract string Name { get; }
	public virtual void Down(PaintSession s, Vector2 p) { }
	public virtual void Move(PaintSession s, Vector2 p) { }
	public virtual void Up(PaintSession s) { }

	// Mouse movement with no button held (for tools that place points by clicking)
	public virtual void Hover(PaintSession s, Vector2 p) { }

	// True while the tool has unfinished work spanning several clicks
	public virtual bool InProgress => false;

	// Called when switching away from this tool, to finish any pending work
	public virtual void Deactivate(PaintSession s) { }

	// Alt+click picks a color with most tools; tools that use Alt themselves say so here
	public virtual bool UsesAlt => false;
}


public sealed class EyedropperTool : PaintTool
{
	public override string Name => "Eyedropper";
	public override void Down(PaintSession s, Vector2 p) => Pick(s, p);
	public override void Move(PaintSession s, Vector2 p) => Pick(s, p);   // drag to scrub colors

	private static void Pick(PaintSession s, Vector2 p)
	{
		if (s.Composite is not { } img) return;
		int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
		if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) return;
		var c = img.GetPixel(x, y);       // samples the merged image, like "sample all layers"
		if (c.Alpha > 0) s.SetColor(c);
	}
}

// Bucket fill. The area is painted OVER what's there, so a see-through colour tints it
// instead of punching a hole.
public sealed class FillTool : PaintTool
{
	public override string Name => "Fill";

	public override void Down(PaintSession s, Vector2 p)
	{
		if (s.LayerBitmap is not { } layer || s.Asset == null) return;
		int w = layer.Width, h = layer.Height;
		int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
		if (x < 0 || y < 0 || x >= w || y >= h) return;
		if (!s.InSelection(x, y)) return;

		var before = PixelBuffer.Read(layer);
		// Compare against the merged image or the layer itself; always write into the layer
		var sample = s.SampleAllLayers && s.Composite is { } merged ? PixelBuffer.Read(merged) : before;
		if (s.Color.Alpha == 255 && sample.Data[y * w + x] == PixelBuffer.Encode(s.Color)) return;   // already that colour

		// The same search as the wand (connected, similar pixels), kept inside the selection
		var area = MagicWand.Select(sample, x, y, s.Tolerance, contiguous: true, allowed: s.InSelection);
		using var mask = new SKBitmap(AssetStore.InfoFor(w, h));
		mask.Erase(SKColors.Transparent);
		var maskPixels = PixelBuffer.Read(mask);
		int white = PixelBuffer.Encode(SKColors.White);
		for (int i = 0; i < area.Length; i++)
			if (area[i]) maskPixels.Data[i] = white;
		maskPixels.WriteTo(mask);

		MaskCompositor.ApplyColors(s, layer, before, (mask, s.Color));
		s.Asset.MarkEdited();
		s.CommitPixels("Fill", before);
	}
}

// Moves the selected pixels (any shape), or the whole layer when nothing is selected.
// The selection travels with them; pixels and selection undo together.
public sealed class MoveTool : PaintTool
{
	public override string Name => "Move";

	private PixelBuffer? _before;
	private SKBitmap? _floating;          // the pixels being carried (the rest transparent)
	private SelectionMask? _maskAtStart;
	private Vector2 _start;
	private (int X, int Y) _offset;

	public override void Down(PaintSession s, Vector2 p)
	{
		if (s.LayerBitmap is not { } layer) return;
		_before = PixelBuffer.Read(layer);
		_maskAtStart = s.Selection;
		_start = p;
		_offset = (0, 0);

		// Copy the selected pixels out of the layer, so they can be lifted and dropped elsewhere
		_floating = new SKBitmap(AssetStore.InfoFor(layer.Width, layer.Height));
		_floating.Erase(SKColors.Transparent);
		using var c = new SKCanvas(_floating);
		if (_maskAtStart is { } mask) c.ClipRegion(mask.Region);
		c.DrawBitmap(layer, 0, 0, AssetStore.Nearest, null);
	}

	public override void Move(PaintSession s, Vector2 p)
	{
		if (_before == null || _floating == null || s.LayerBitmap is not { } layer || s.Asset == null) return;

		// Whole-pixel steps keep pixel art crisp
		int dx = (int)MathF.Round(p.X - _start.X);
		int dy = (int)MathF.Round(p.Y - _start.Y);
		if ((dx, dy) == _offset) return;
		_offset = (dx, dy);

		// Same trick as strokes: rebuild from the original every time
		_before.WriteTo(layer);
		using (var c = new SKCanvas(layer))
		{
			c.Save();
			if (_maskAtStart is { } mask) c.ClipRegion(mask.Region);
			c.Clear(SKColors.Transparent);    // leave a hole where the pixels were
			c.Restore();
			c.DrawBitmap(_floating, dx, dy, AssetStore.Nearest, null);
		}

		if (_maskAtStart is { } selection)    // the selection travels with its pixels
			s.Selection = selection.Offset(dx, dy);

		s.Asset.MarkEdited();
	}

	public override void Up(PaintSession s)
	{
		if (_before != null && _offset != (0, 0)) s.CommitMove(_before, _maskAtStart);
		_floating?.Dispose();
		_floating = null;
		_before = null;
		_maskAtStart = null;
	}
}

// Drag from the current color to transparent
public sealed class GradientTool : PaintTool
{
	public override string Name => "Gradient";

	public bool Dragging { get; private set; }     // the canvas draws a guide line while true
	public Vector2 Start { get; private set; }
	public Vector2 End { get; private set; }
	private PixelBuffer? _before;

	public override void Down(PaintSession s, Vector2 p)
	{
		if (s.LayerBitmap is not { } layer) return;
		_before = PixelBuffer.Read(layer);
		Start = End = p;
		Dragging = true;
	}

	public override void Move(PaintSession s, Vector2 p)
	{
		if (_before == null || s.LayerBitmap is not { } layer || s.Asset == null) return;
		End = p;
		if (Vector2.Distance(Start, End) < 0.5f) return;

		_before.WriteTo(layer);
		using var c = new SKCanvas(layer);
		s.ClipToSelection(c);
		using var shader = SKShader.CreateLinearGradient(
			new SKPoint(Start.X, Start.Y), new SKPoint(End.X, End.Y),
			new[] { s.Color, s.GradientToTransparent ? s.Color.WithAlpha(0) : s.SecondaryColor }, null, SKShaderTileMode.Clamp);
		using var paint = new SKPaint { Shader = shader };
		c.DrawRect(0, 0, layer.Width, layer.Height, paint);
		s.Asset.MarkEdited();
	}

	public override void Up(PaintSession s)
	{
		if (_before != null && Vector2.Distance(Start, End) >= 0.5f) s.CommitPixels("Gradient", _before);
		_before = null;
		Dragging = false;
	}
}

// Photoshop-style mixer brush: picks up canvas color and blends it into its paint
public sealed class MixerTool : PaintTool
{
	public override string Name => "Mixer";

	private const float Flow = 0.35f;   // strength of each dab

	private PixelBuffer? _before;
	private Vector4 _paint;             // paint on the brush, premultiplied RGBA in 0..1
	private Vector2 _last;
	private float _carry;               // distance travelled since the last dab

	public override void Down(PaintSession s, Vector2 p)
	{
		if (s.LayerBitmap is not { } layer) return;
		_before = PixelBuffer.Read(layer);
		_paint = Premul(s.Color);       // start with a fully loaded brush
		_last = p;
		_carry = 0;
		Dab(s, layer, p);
		s.Asset?.MarkEdited();
	}

	public override void Move(PaintSession s, Vector2 p)
	{
		if (_before == null || s.LayerBitmap is not { } layer) return;

		// Place dabs at even spacing along the mouse path, whatever the mouse speed
		float spacing = MathF.Max(1f, s.Size * 0.15f);
		var dir = p - _last;
		float len = dir.Length();
		if (len <= 0) return;
		dir /= len;

		float d = spacing - _carry;
		while (d <= len)
		{
			Dab(s, layer, _last + dir * d);
			d += spacing;
		}
		_carry = len - (d - spacing);
		_last = p;
		s.Asset?.MarkEdited();
	}

	public override void Up(PaintSession s)
	{
		if (_before != null) s.CommitPixels("Mixer stroke", _before);
		_before = null;
	}

	private void Dab(PaintSession s, SKBitmap layer, Vector2 at)
	{
		float r = MathF.Max(0.5f, s.Size / 2f);

		// 1. Pick up paint from the canvas (Wet)
		// Sample all layers: read the merged image, refreshed so it includes the dabs so far
		var source = layer;
		if (s.SampleAllLayers && s.Asset != null)
		{
			s.Asset.MarkEdited();
			source = s.Composite ?? layer;
		}
		if (SampleUnder(source, at, r) is { } under)
			_paint = Vector4.Lerp(_paint, under, s.MixWet);

		// 2. Refill from the selected color (Load)
		_paint = Vector4.Lerp(_paint, Premul(s.Color), s.MixLoad);
		if (_paint.W < 0.004f) return;   // brush has run dry

		// 3. Put down a soft dab of the mixed paint
		var color = Unpremul(_paint);
		using var c = new SKCanvas(layer);
		s.ClipToSelection(c);
		using var shader = SKShader.CreateRadialGradient(
			new SKPoint(at.X, at.Y), r,
			new[] { color, color.WithAlpha(0) }, new[] { 0.4f, 1f }, SKShaderTileMode.Clamp);
		using var paint = new SKPaint
		{
			Shader = shader,
			IsAntialias = true,
			Color = SKColors.White.WithAlpha((byte)(Flow * 255))
		};
		c.DrawCircle(at.X, at.Y, r, paint);
	}

	// Average color under the brush (premultiplied), or null if the brush is off-canvas
	private static Vector4? SampleUnder(SKBitmap layer, Vector2 at, float r)
	{
		float rr = MathF.Max(r, 1f);
		int step = Math.Max(1, (int)(rr / 3));   // a sparse grid is plenty for an average
		var sum = Vector4.Zero;
		int n = 0;

		for (int y = (int)(at.Y - rr); y <= (int)(at.Y + rr); y += step)
			for (int x = (int)(at.X - rr); x <= (int)(at.X + rr); x += step)
			{
				if (x < 0 || y < 0 || x >= layer.Width || y >= layer.Height) continue;
				if (Vector2.DistanceSquared(new Vector2(x + 0.5f, y + 0.5f), at) > rr * rr) continue;
				sum += Premul(layer.GetPixel(x, y));
				n++;
			}

		return n == 0 ? null : sum / n;
	}

	private static Vector4 Premul(SKColor c)
	{
		float a = c.Alpha / 255f;
		return new Vector4(c.Red / 255f * a, c.Green / 255f * a, c.Blue / 255f * a, a);
	}

	private static SKColor Unpremul(Vector4 v)
	{
		if (v.W <= 0) return SKColors.Transparent;
		return new SKColor(ToByte(v.X / v.W), ToByte(v.Y / v.W), ToByte(v.Z / v.W), ToByte(v.W));
	}

	private static byte ToByte(float f) => (byte)Math.Clamp(MathF.Round(f * 255f), 0, 255);
}