using Animator.Viewer.Painting;
using Animator.Viewer.Rendering;
using SkiaSharp;

namespace Animator.Tests;

// Selections of any shape, the magic wand, and the colour picker's maths
public class PaintToolTests
{
	private static SelectionMask Box(int left, int top, int right, int bottom, int size = 10)
	{
		using var path = ShapePaths.Rect(new SKRect(left, top, right, bottom));
		return SelectionMask.FromPath(size, size, path);
	}

	[Fact]
	public void A_rectangle_selects_the_pixels_inside_it()
	{
		var mask = Box(2, 3, 5, 7);

		Assert.Equal(new SKRectI(2, 3, 5, 7), mask.Bounds);
		Assert.True(mask.Contains(2, 3));
		Assert.True(mask.Contains(4, 6));
		Assert.False(mask.Contains(5, 6));
		Assert.False(mask.Contains(1, 3));
	}

	[Fact]
	public void Shapes_combine_like_in_photoshop()
	{
		var left = Box(0, 0, 6, 10);
		var right = Box(4, 0, 10, 10);

		var added = SelectionMask.Combine(left, right, SelectMode.Add)!;
		var subtracted = SelectionMask.Combine(left, right, SelectMode.Subtract)!;
		var intersected = SelectionMask.Combine(left, right, SelectMode.Intersect)!;

		Assert.Equal(new SKRectI(0, 0, 10, 10), added.Bounds);
		Assert.Equal(new SKRectI(0, 0, 4, 10), subtracted.Bounds);
		Assert.Equal(new SKRectI(4, 0, 6, 10), intersected.Bounds);
		Assert.Same(right, SelectionMask.Combine(left, right, SelectMode.New));
	}

	[Fact]
	public void With_nothing_selected_add_starts_a_selection_and_subtract_leaves_nothing()
	{
		var shape = Box(1, 1, 3, 3);

		Assert.Same(shape, SelectionMask.Combine(null, shape, SelectMode.Add));
		Assert.Null(SelectionMask.Combine(null, shape, SelectMode.Subtract));
		Assert.Null(SelectionMask.Combine(shape, shape, SelectMode.Subtract));   // all of it taken away
	}

	[Fact]
	public void Inverting_and_moving_a_selection()
	{
		var mask = Box(0, 0, 5, 10);

		Assert.Equal(new SKRectI(5, 0, 10, 10), mask.Inverted()!.Bounds);
		Assert.Null(SelectionMask.All(10, 10).Inverted());

		var moved = mask.Offset(3, -2);   // the part pushed off the top is dropped
		Assert.Equal(new SKRectI(3, 0, 8, 8), moved.Bounds);
		Assert.True(moved.Contains(7, 7));
		Assert.False(moved.Contains(2, 0));
	}

	[Fact]
	public void The_outline_and_clip_region_can_be_built()
	{
		var mask = SelectionMask.Combine(Box(0, 0, 4, 4), Box(6, 6, 9, 9), SelectMode.Add)!;

		Assert.False(mask.Region.IsEmpty);
		Assert.Equal(new SKRectI(0, 0, 9, 9), mask.Region.Bounds);
		Assert.False(mask.Outline.IsEmpty);
	}

	// A 6×3 strip: two red pixels, a gap, then two more red pixels and a dark red one
	private static PixelBuffer Strip()
	{
		using var bitmap = new SKBitmap(AssetStore.InfoFor(6, 3));
		bitmap.Erase(SKColors.Transparent);
		foreach (int x in new[] { 0, 1, 3, 4 })
			for (int y = 0; y < 3; y++)
				bitmap.SetPixel(x, y, new SKColor(220, 30, 30));
		for (int y = 0; y < 3; y++) bitmap.SetPixel(5, y, new SKColor(200, 30, 30));
		return PixelBuffer.Read(bitmap);
	}

	private static int Count(bool[] bits) => bits.Count(b => b);

	[Fact]
	public void The_wand_selects_the_connected_area_of_a_colour()
	{
		var bits = MagicWand.Select(Strip(), 0, 0, tolerance: 0, contiguous: true);

		Assert.Equal(6, Count(bits));   // columns 0 and 1 only: column 2 is empty
		Assert.True(bits[1]);
		Assert.False(bits[3]);
	}

	[Fact]
	public void The_wand_can_take_a_colour_everywhere_and_tolerance_widens_it()
	{
		Assert.Equal(12, Count(MagicWand.Select(Strip(), 0, 0, tolerance: 0, contiguous: false)));
		Assert.Equal(15, Count(MagicWand.Select(Strip(), 0, 0, tolerance: 30, contiguous: false)));
		Assert.Equal(9, Count(MagicWand.Select(Strip(), 3, 0, tolerance: 30, contiguous: true)));
	}

	[Fact]
	public void Hue_saturation_value_round_trips()
	{
		Assert.Equal(new SKColor(255, 0, 0), ColorMath.FromHsv(0, 1, 1));
		Assert.Equal(new SKColor(0, 255, 0), ColorMath.FromHsv(120, 1, 1));
		Assert.Equal(new SKColor(0, 0, 255, 128), ColorMath.FromHsv(240, 1, 1, 128));

		foreach (var color in new[] { new SKColor(12, 200, 99), new SKColor(250, 250, 3), new SKColor(77, 77, 77) })
		{
			var (h, s, v) = ColorMath.ToHsv(color);
			Assert.Equal(color, ColorMath.FromHsv(h, s, v));
		}
	}
	// A 4×4 drawing filled solid blue, open in a paint session with the bucket
	private static (PaintSession Session, SKBitmap Pixels) BlueDrawing()
	{
		var store = new AssetStore();
		var asset = new Asset { Name = "box", Width = 4, Height = 4 };
		var layer = store.AddLayer(asset, "Base");
		var session = new PaintSession(store, new UndoStack(), new Skeleton(), new FillTool());
		session.Open(asset);
		var pixels = store.GetLayer(layer)!;
		pixels.Erase(new SKColor(0, 0, 255));
		return (session, pixels);
	}

	[Fact]
	public void A_see_through_fill_tints_instead_of_replacing()
	{
		var (session, pixels) = BlueDrawing();
		session.SetColor(new SKColor(255, 0, 0, 128));   // half see-through red

		session.Tool.Down(session, new Vector2(1, 1));

		var pixel = pixels.GetPixel(1, 1);
		Assert.Equal(255, pixel.Alpha);   // still solid: red over blue, not a see-through hole
		Assert.InRange(pixel.Red, 120, 136);
		Assert.InRange(pixel.Blue, 120, 136);
	}

	[Fact]
	public void The_bucket_stays_inside_the_selection()
	{
		var (session, pixels) = BlueDrawing();
		using (var left = ShapePaths.Rect(new SKRect(0, 0, 2, 4)))
			session.Selection = SelectionMask.FromPath(4, 4, left);
		session.SetColor(SKColors.Red);

		session.Tool.Down(session, new Vector2(0, 0));

		Assert.Equal(SKColors.Red, pixels.GetPixel(1, 3));
		Assert.Equal(new SKColor(0, 0, 255), pixels.GetPixel(2, 0));
	}
}
