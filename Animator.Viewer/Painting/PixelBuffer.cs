using System.Runtime.InteropServices;
using SkiaSharp;

namespace Animator.Viewer.Painting;

// A copy of a bitmap's pixels as plain integers (one int = one pixel).
// Used for undo snapshots and the fill tool.
public sealed class PixelBuffer
{
	public int Width { get; }
	public int Height { get; }
	public int[] Data { get; }

	private PixelBuffer(int width, int height, int[] data)
	{
		Width = width; Height = height; Data = data;
	}

	public static PixelBuffer Read(SKBitmap bmp)
	{
		var data = new int[bmp.Width * bmp.Height];
		Marshal.Copy(bmp.GetPixels(), data, 0, data.Length);   // copy out of Skia's memory
		return new PixelBuffer(bmp.Width, bmp.Height, data);
	}

	// Writes back IN PLACE, so the layer keeps the same bitmap object
	public void WriteTo(SKBitmap bmp)
	{
		if (bmp.Width != Width || bmp.Height != Height) return;   // never write into a different-sized bitmap
		Marshal.Copy(Data, 0, bmp.GetPixels(), Data.Length);
		bmp.NotifyPixelsChanged();
	}

	public PixelBuffer Clone() => new(Width, Height, (int[])Data.Clone());

	// A color converted to the exact raw format our layer bitmaps use
	public static int Encode(SKColor color)
	{
		using var one = new SKBitmap(AssetStore.InfoFor(1, 1));
		one.Erase(color);
		var d = new int[1];
		Marshal.Copy(one.GetPixels(), d, 0, 1);
		return d[0];
	}
}