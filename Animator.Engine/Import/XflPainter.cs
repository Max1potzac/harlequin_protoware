using System.IO;
using System.IO.Compression;
using System.Numerics;
using SkiaSharp;

namespace Animator.Viewer.Import;

// Draws an Animate project's drawings with Skia: symbols that never change, library pictures
// (PNG, JPEG, GIF, or Animate's own bin/*.dat), and vector shapes with their fills (colours,
// gradients, pictures) and strokes. Everything is in the drawing's own space (pixels, y down).
public sealed class XflPainter : IDisposable
{
	private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

	private readonly XflDocument _doc;
	private readonly XflEvaluator _evaluator;
	private readonly Dictionary<string, SKBitmap?> _bitmaps = new(StringComparer.Ordinal);
	public HashSet<string> Notes { get; } = new();

	public XflPainter(XflDocument doc, XflEvaluator evaluator)
	{
		_doc = doc;
		_evaluator = evaluator;
	}

	public void Dispose()
	{
		foreach (var bitmap in _bitmaps.Values) bitmap?.Dispose();
		_bitmaps.Clear();
	}

	// ======================= pictures =======================

	public SKBitmap? Bitmap(string name)
	{
		if (_bitmaps.TryGetValue(name, out var known)) return known;
		SKBitmap? bitmap = null;
		if (_doc.Bitmaps.TryGetValue(name, out var item) && _doc.BitmapBytes(item) is { } bytes)
		{
			try
			{
				bitmap = Decode(bytes);
			}
			catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
			{
				bitmap = null;
			}
		}
		if (bitmap == null) Notes.Add($"Some pictures couldn't be read (\"{name}\"): they're left out.");
		_bitmaps[name] = bitmap;
		return bitmap;
	}

	// PNG, JPEG, GIF, or Animate's own lossless format (in bin/*.dat)
	public static SKBitmap? Decode(byte[] bytes)
	{
		if (bytes.Length > 26 && bytes[1] == 5 && bytes[0] <= 3) return DecodeDat(bytes);
		using var decoded = SKBitmap.Decode(bytes);
		return decoded == null ? null : Converted(decoded);
	}

	// Animate's lossless pictures (reverse-engineered by JPEXS): a little-endian header (format,
	// row size, width, height, frame), then an alpha flag and a compression flag, then the pixels
	// (alpha, red, green, blue, premultiplied in Animate's own way), zlib-compressed in chunks
	public static SKBitmap? DecodeDat(byte[] data)
	{
		int major = data[0];
		int width = data[4] | data[5] << 8, height = data[6] | data[7] << 8;
		if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null;
		int p = 24;
		bool hasAlpha = false;
		if (major >= 3) hasAlpha = (data[p++] & 1) == 1;
		byte[] pixels;
		bool compressed = major >= 2 && data[p++] == 1;
		if (compressed)
		{
			using var chunks = new MemoryStream();
			while (p + 2 <= data.Length)
			{
				int length = data[p] | data[p + 1] << 8;
				p += 2;
				if (length == 0 || p + length > data.Length) break;
				chunks.Write(data, p, length);
				p += length;
			}
			chunks.Position = 0;
			using var inflate = new ZLibStream(chunks, CompressionMode.Decompress);
			using var output = new MemoryStream();
			inflate.CopyTo(output);
			pixels = output.ToArray();
		}
		else
		{
			pixels = data[p..];
		}
		if (pixels.Length < width * height * 4) return null;

		var rgba = new byte[width * height * 4];
		for (int i = 0; i < width * height; i++)
		{
			int a = pixels[i * 4], r = pixels[i * 4 + 1], g = pixels[i * 4 + 2], b = pixels[i * 4 + 3];
			if (a != 0 && a != 255)
			{
				a--;
				r = Math.Min(255, r * 256 / Math.Max(1, a));
				g = Math.Min(255, g * 256 / Math.Max(1, a));
				b = Math.Min(255, b * 256 / Math.Max(1, a));
			}
			if (!hasAlpha) a = 255;
			rgba[i * 4] = (byte)r;
			rgba[i * 4 + 1] = (byte)g;
			rgba[i * 4 + 2] = (byte)b;
			rgba[i * 4 + 3] = (byte)a;
		}
		return FromRgba(rgba, width, height);
	}

	public static SKBitmap FromRgba(byte[] rgba, int width, int height)
	{
		using var straight = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
		System.Runtime.InteropServices.Marshal.Copy(rgba, 0, straight.GetPixels(), width * height * 4);
		straight.NotifyPixelsChanged();
		return Converted(straight);
	}

	// In the editor's own pixel format
	private static SKBitmap Converted(SKBitmap source)
	{
		var bitmap = new SKBitmap(AssetStore.InfoFor(source.Width, source.Height));
		bitmap.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(bitmap))
			canvas.DrawBitmap(source, 0, 0, AssetStore.Nearest, null);
		return bitmap;
	}

	// ======================= drawings =======================

	// How far a drawing reaches, in its own space (empty if it draws nothing)
	public SKRect Bounds(XflDrawingKey key)
	{
		var bounds = SKRect.Empty;
		Visit(key, null, SKMatrix.Identity, ref bounds);
		return bounds;
	}

	// Draws a drawing on the canvas, in its own space (the canvas's current transform)
	public void Draw(SKCanvas canvas, XflDrawingKey key)
	{
		var unused = SKRect.Empty;
		Visit(key, canvas, SKMatrix.Identity, ref unused);
	}

	// The drawing as a picture, `scale` pixels per unit; Origin is where its top-left pixel
	// corner is in the drawing's space. Null when it draws nothing.
	public (SKBitmap Image, Vector2 Origin)? Render(XflDrawingKey key, float scale)
	{
		var bounds = Bounds(key);
		if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
		var origin = new Vector2(MathF.Floor(bounds.Left * scale) / scale, MathF.Floor(bounds.Top * scale) / scale);
		int width = Math.Clamp((int)MathF.Ceiling((bounds.Right - origin.X) * scale) + 1, 1, 8192);
		int height = Math.Clamp((int)MathF.Ceiling((bounds.Bottom - origin.Y) * scale) + 1, 1, 8192);
		var image = new SKBitmap(AssetStore.InfoFor(width, height));
		image.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(image))
		{
			canvas.Scale(scale);
			canvas.Translate(-origin.X, -origin.Y);
			Draw(canvas, key);
		}
		return (image, origin);
	}

	// How many pixels per unit the pictures inside a drawing have (1 when there are none), so it
	// can be drawn with all their detail
	public float PictureDensity(XflDrawingKey key)
	{
		float density = 0;
		void Look(IEnumerable<XflElement> elements, Matrix3x2 m, int depth)
		{
			if (depth > 16) return;
			foreach (var e in elements)
			{
				var placed = e is XflGroup ? m : e.Matrix * m;
				switch (e)
				{
					case XflBitmapInstance:
						float sx = new Vector2(placed.M11, placed.M12).Length(), sy = new Vector2(placed.M21, placed.M22).Length();
						if (sx > 1e-4f && sy > 1e-4f) density = MathF.Max(density, 1f / MathF.Min(sx, sy));
						break;
					case XflInstance i when _doc.Symbol(i.Library) is { } s:
						Look(FrameElements(s.Timeline, FrameOf(i, s)), placed, depth + 1);
						break;
					case XflGroup g:
						Look(g.Members, m, depth + 1);
						break;
				}
			}
		}
		Look(Elements(key), Matrix3x2.Identity, 0);
		return density > 0 ? density : 1f;
	}

	private IEnumerable<XflElement> Elements(XflDrawingKey key) => key.Kind switch
	{
		XflDrawingKind.Symbol => _doc.Symbol(key.Name) is { } s ? FrameElements(s.Timeline, 0) : Enumerable.Empty<XflElement>(),
		XflDrawingKind.Bitmap => new XflElement[] { new XflBitmapInstance { Library = key.Name } },
		_ => _evaluator.RunElements(key)
	};

	// Everything on a timeline's frame, bottom layer first
	private static IEnumerable<XflElement> FrameElements(XflTimeline timeline, int frame)
	{
		for (int li = timeline.Layers.Count - 1; li >= 0; li--)
		{
			var layer = timeline.Layers[li];
			if (layer.Kind != XflLayerKind.Normal) continue;
			int k = layer.KeyframeAt(frame);
			if (k < 0) continue;
			foreach (var e in layer.Frames[k].Elements) yield return e;
		}
	}

	// The frame of a symbol shown inside a drawing that doesn't change: its first
	private static int FrameOf(XflInstance instance, XflSymbol symbol) =>
		instance.Kind == XflSymbolKind.Graphic ? Math.Clamp(instance.FirstFrame, 0, symbol.Timeline.Length - 1) : 0;

	// Draws (canvas set) or measures (canvas null) a drawing's elements through `m`
	private void Visit(XflDrawingKey key, SKCanvas? canvas, SKMatrix m, ref SKRect bounds) =>
		VisitElements(Elements(key), canvas, m, ref bounds, 0);

	private void VisitElements(IEnumerable<XflElement> elements, SKCanvas? canvas, SKMatrix m, ref SKRect bounds, int depth)
	{
		if (depth > 32) return;
		foreach (var e in elements)
		{
			switch (e)
			{
				case XflBitmapInstance b:
				{
					if (Bitmap(b.Library) is not { } picture) break;
					var placed = Concat(m, e.Matrix);
					if (canvas == null) Grow(ref bounds, placed.MapRect(new SKRect(0, 0, picture.Width, picture.Height)));
					else
					{
						canvas.Save();
						canvas.Concat(SkeletonRenderer.ToSk(e.Matrix));
						canvas.DrawBitmap(picture, 0, 0, Smooth, null);
						canvas.Restore();
					}
					break;
				}
				case XflInstance i when _doc.Symbol(i.Library) is { } symbol:
				{
					var placed = Concat(m, e.Matrix);
					var inside = FrameElements(symbol.Timeline, FrameOf(i, symbol));
					if (canvas == null)
					{
						VisitElements(inside, null, placed, ref bounds, depth + 1);
						break;
					}
					canvas.Save();
					canvas.Concat(SkeletonRenderer.ToSk(e.Matrix));
					SKColorFilter? filter = null;
					SKPaint? layer = null;
					if (!i.Color.IsIdentity)
					{
						filter = ColorFilter(i.Color);
						layer = new SKPaint { ColorFilter = filter };
						canvas.SaveLayer(layer);
					}
					VisitElements(inside, canvas, placed, ref bounds, depth + 1);
					if (layer != null) canvas.Restore();
					canvas.Restore();
					layer?.Dispose();
					filter?.Dispose();
					break;
				}
				case XflGroup g:
					VisitElements(g.Members, canvas, m, ref bounds, depth + 1);   // a group's own matrix isn't used
					break;
				case XflShape s:
					VisitShape(s, canvas, m, ref bounds);
					break;
			}
		}
	}

	private static SKMatrix Concat(SKMatrix outer, Matrix3x2 inner) => SKMatrix.Concat(outer, SkeletonRenderer.ToSk(inner));

	private static void Grow(ref SKRect bounds, SKRect add)
	{
		if (add.IsEmpty && add.Width <= 0 && add.Height <= 0) return;
		bounds = bounds.IsEmpty ? add : SKRect.Union(bounds, add);
	}

	// ======================= shapes =======================

	private void VisitShape(XflShape shape, SKCanvas? canvas, SKMatrix m, ref SKRect bounds)
	{
		var (fills, strokes) = XflShapeGeometry.Build(shape);
		if (canvas != null)
		{
			canvas.Save();
			canvas.Concat(SkeletonRenderer.ToSk(shape.Matrix));
		}
		var placed = Concat(m, shape.Matrix);
		foreach (var (style, loops) in fills)
		{
			using var path = Path(loops, closeAll: true);
			if (canvas == null) Grow(ref bounds, placed.MapRect(path.Bounds));
			else Fill(canvas, path, shape.Fills[style]);
		}
		foreach (var (style, runs) in strokes)
		{
			var stroke = shape.Strokes[style];
			using var path = Path(runs, closeAll: false);
			if (canvas == null)
			{
				var box = path.Bounds;
				float half = stroke.Hairline ? 0.5f : stroke.Weight / 2;
				box.Inflate(half, half);
				Grow(ref bounds, placed.MapRect(box));
				continue;
			}
			using var paint = new SKPaint
			{
				IsAntialias = true,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = stroke.Hairline ? 0 : MathF.Max(0.05f, stroke.Weight),
				StrokeCap = stroke.Caps switch { XflCaps.None => SKStrokeCap.Butt, XflCaps.Square => SKStrokeCap.Square, _ => SKStrokeCap.Round },
				StrokeJoin = stroke.Joints switch { XflJoints.Bevel => SKStrokeJoin.Bevel, XflJoints.Miter => SKStrokeJoin.Miter, _ => SKStrokeJoin.Round },
				StrokeMiter = MathF.Max(1, stroke.MiterLimit)
			};
			if (stroke.Paint is XflBitmapFill)
			{
				Notes.Add("Strokes filled with a picture are drawn grey.");
				paint.Color = new SKColor(128, 128, 128);
				canvas.DrawPath(path, paint);
			}
			else
			{
				using var shader = Shader(stroke.Paint, paint);
				canvas.DrawPath(path, paint);
			}
		}
		if (canvas != null) canvas.Restore();
	}

#pragma warning disable CS0618   // SKPath's building methods: marked obsolete in this SkiaSharp, but they work (see ShapePaths)
	private static SKPath Path(List<XflContour> contours, bool closeAll)
	{
		var path = new SKPath { FillType = SKPathFillType.EvenOdd };
		foreach (var contour in contours)
		{
			path.MoveTo(contour.Start.X, contour.Start.Y);
			foreach (var curve in contour.Curves)
			{
				if (curve.Quad) path.QuadTo(curve.Control.X, curve.Control.Y, curve.End.X, curve.End.Y);
				else path.LineTo(curve.End.X, curve.End.Y);
			}
			if (closeAll || contour.Closed) path.Close();
		}
		return path;
	}
#pragma warning restore CS0618

	private void Fill(SKCanvas canvas, SKPath path, XflPaint fill)
	{
		using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
		if (fill is XflBitmapFill picture)
		{
			Pictured(canvas, path, picture, paint);
			return;
		}
		using var shader = Shader(fill, paint);
		canvas.DrawPath(path, paint);
	}

	// Sets the paint's colour or gradient; returns the shader to dispose (if any)
	private static SKShader? Shader(XflPaint fill, SKPaint paint)
	{
		switch (fill)
		{
			case XflSolid solid:
				paint.Color = ToColor(solid.Color);
				return null;
			case XflGradient g:
				var colors = g.Stops.Select(s => ToColor(s.Color)).ToArray();
				var positions = g.Stops.Select(s => s.Ratio).ToArray();
				var mode = g.Spread switch { XflSpread.Reflect => SKShaderTileMode.Mirror, XflSpread.Repeat => SKShaderTileMode.Repeat, _ => SKShaderTileMode.Clamp };
				var local = SkeletonRenderer.ToSk(g.Matrix);
				var shader = g.Radial
					? SKShader.CreateTwoPointConicalGradient(new SKPoint(g.FocalPoint * XflGradient.Half, 0), 0,
						new SKPoint(0, 0), XflGradient.Half, colors, positions, mode, local)
					: SKShader.CreateLinearGradient(new SKPoint(-XflGradient.Half, 0), new SKPoint(XflGradient.Half, 0),
						colors, positions, mode, local);
				paint.Shader = shader;
				paint.Color = SKColors.Black;
				return shader;
			default:
				paint.Color = SKColors.Black;
				return null;
		}
	}

	// A picture fill: the path clips the picture, repeated across it unless it's clipped
	private void Pictured(SKCanvas canvas, SKPath path, XflBitmapFill fill, SKPaint paint)
	{
		if (Bitmap(fill.Bitmap) is not { } picture) return;
		canvas.Save();
		canvas.ClipPath(path, SKClipOperation.Intersect, true);
		var toPicture = SkeletonRenderer.ToSk(fill.Matrix);
		canvas.Concat(toPicture);
		if (fill.Clipped || !toPicture.TryInvert(out var back))
		{
			canvas.DrawBitmap(picture, 0, 0, Smooth, null);
		}
		else
		{
			var area = back.MapRect(path.Bounds);
			int x0 = (int)MathF.Floor(area.Left / picture.Width), x1 = (int)MathF.Ceiling(area.Right / picture.Width);
			int y0 = (int)MathF.Floor(area.Top / picture.Height), y1 = (int)MathF.Ceiling(area.Bottom / picture.Height);
			if ((long)(x1 - x0) * (y1 - y0) <= 4096)
				for (int ty = y0; ty < y1; ty++)
					for (int tx = x0; tx < x1; tx++)
						canvas.DrawBitmap(picture, tx * picture.Width, ty * picture.Height, Smooth, null);
		}
		canvas.Restore();
	}

	private static SKColor ToColor(Vector4 c) =>
		new((byte)Math.Clamp(c.X * 255 + 0.5f, 0, 255), (byte)Math.Clamp(c.Y * 255 + 0.5f, 0, 255),
			(byte)Math.Clamp(c.Z * 255 + 0.5f, 0, 255), (byte)Math.Clamp(c.W * 255 + 0.5f, 0, 255));

	// A colour effect as a Skia colour matrix (offsets 0..1)
	public static SKColorFilter ColorFilter(XflColor c) => SKColorFilter.CreateColorMatrix(new[]
	{
		c.Multiply.X, 0, 0, 0, c.Offset.X / 255f,
		0, c.Multiply.Y, 0, 0, c.Offset.Y / 255f,
		0, 0, c.Multiply.Z, 0, c.Offset.Z / 255f,
		0, 0, 0, c.Multiply.W, c.Offset.W / 255f
	});
}
