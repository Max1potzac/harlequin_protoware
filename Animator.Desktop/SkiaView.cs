using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Animator.Desktop;

// A control the engine draws on with Skia (the viewport, the timeline): every time it's drawn,
// Draw gets a canvas in device pixels, the control's size in screen units and the number of
// pixels to a unit. Drawn into a bitmap of its own (on the CPU, like the Windows editor's
// SKElement), so it looks the same on every system and in the headless tests.
public sealed class SkiaView : Control
{
	private WriteableBitmap? _bitmap;

	public Action<SKCanvas, Vector2, float>? Draw { get; set; }

	public SkiaView()
	{
		Focusable = true;
		ClipToBounds = true;
	}

	public Vector2 SizeInUnits => new((float)Bounds.Width, (float)Bounds.Height);

	public override void Render(DrawingContext context)
	{
		var top = TopLevel.GetTopLevel(this);
		double scaling = top?.RenderScaling ?? 1.0;
		// Inside something scaled (UiSize): as many pixels as it covers on the screen
		if (top != null && this.TransformToVisual(top) is { } m)
			scaling *= Math.Max(0.1, Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12));
		int width = (int)Math.Ceiling(Bounds.Width * scaling);
		int height = (int)Math.Ceiling(Bounds.Height * scaling);
		if (width <= 0 || height <= 0 || Draw == null) return;

		if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
		{
			_bitmap?.Dispose();
			_bitmap = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96 * scaling, 96 * scaling),
				PixelFormat.Bgra8888, AlphaFormat.Premul);
		}

		using (var frame = _bitmap.Lock())
		{
			var info = new SKImageInfo(frame.Size.Width, frame.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
			using var surface = SKSurface.Create(info, frame.Address, frame.RowBytes);
			if (surface != null) Draw(surface.Canvas, SizeInUnits, (float)(frame.Size.Width / Math.Max(Bounds.Width, 1e-6)));
		}
		context.DrawImage(_bitmap, new Rect(0, 0, Bounds.Width, Bounds.Height));
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		_bitmap?.Dispose();
		_bitmap = null;
	}
}
