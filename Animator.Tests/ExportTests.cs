using System.IO;
using Animator.Core.Export;
using Animator.Viewer.Export;
using Animator.Viewer.Rendering;
using SkiaSharp;

namespace Animator.Tests;

// Milestone 10: GIFs, sprite sheets, and exporting pictures of the scene or of an object
public class ExportTests
{
	// ======================= GIF =======================

	private static byte[] Pixels(params SKColor[] colors)
	{
		var bytes = new byte[colors.Length * 4];
		for (int i = 0; i < colors.Length; i++)
		{
			bytes[i * 4] = colors[i].Red;
			bytes[i * 4 + 1] = colors[i].Green;
			bytes[i * 4 + 2] = colors[i].Blue;
			bytes[i * 4 + 3] = colors[i].Alpha;
		}
		return bytes;
	}

	private static byte[] WriteGif(int width, int height, int delay, bool loop, params byte[][] frames)
	{
		var palette = new GifPalette();
		foreach (var f in frames) palette.Add(f);
		palette.Build();
		using var stream = new MemoryStream();
		using (var gif = new GifWriter(stream, width, height, palette, loop, dither: false))
			foreach (var f in frames) gif.AddFrame(f, delay);
		return stream.ToArray();
	}

	// Decodes one frame of a GIF with SkiaSharp (an independent reader)
	private static SKBitmap Decode(byte[] gif, int frame, out SKCodec codec)
	{
		codec = SKCodec.Create(new MemoryStream(gif)) ?? throw new InvalidDataException("not a GIF");
		var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
		var bitmap = new SKBitmap(info);
		var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(frame));
		Assert.True(result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput, result.ToString());
		return bitmap;
	}

	[Fact]
	public void A_gif_keeps_its_frames_colours_timing_and_see_through_pixels()
	{
		var red = new SKColor(220, 30, 30);
		var a = Pixels(red, SKColors.Lime, SKColors.Blue, SKColors.White, SKColors.Black, SKColors.Transparent);
		var b = Pixels(SKColors.Blue, SKColors.Blue, SKColors.Blue, red, red, SKColors.Transparent);

		var gif = WriteGif(3, 2, delay: 10, loop: true, a, b);

		using (var first = Decode(gif, 0, out var codec))
		{
			Assert.Equal(2, codec.FrameCount);
			Assert.Equal(100, codec.FrameInfo[0].Duration);   // 10 hundredths = 100 ms
			Assert.Equal(red, first.GetPixel(0, 0));
			Assert.Equal(SKColors.Lime, first.GetPixel(1, 0));
			Assert.Equal(SKColors.Black, first.GetPixel(1, 1));
			Assert.Equal(0, first.GetPixel(2, 1).Alpha);
			codec.Dispose();
		}
		using (var second = Decode(gif, 1, out var codec))
		{
			Assert.Equal(SKColors.Blue, second.GetPixel(0, 0));
			Assert.Equal(red, second.GetPixel(1, 1));
			Assert.Equal(0, second.GetPixel(2, 1).Alpha);
			codec.Dispose();
		}
	}

	[Fact]
	public void A_busy_picture_survives_the_compression_exactly()
	{
		// 100 × 100 pixels of 200 colours in a random order: the code table fills up and starts
		// over several times, and the codes grow from 9 to 12 bits
		var random = new Random(7);
		var colours = Enumerable.Range(0, 200).Select(_ => new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))).ToArray();
		var picture = Enumerable.Range(0, 100 * 100).Select(_ => colours[random.Next(colours.Length)]).ToArray();

		var gif = WriteGif(100, 100, delay: 4, loop: false, Pixels(picture));

		using var decoded = Decode(gif, 0, out var codec);
		codec.Dispose();
		for (int i = 0; i < picture.Length; i++)
		{
			var c = decoded.GetPixel(i % 100, i / 100);
			// Colours sharing a 5-bit box may be merged; everything else must come back exactly
			Assert.True(Math.Abs(c.Red - picture[i].Red) <= 8 && Math.Abs(c.Green - picture[i].Green) <= 8 &&
						Math.Abs(c.Blue - picture[i].Blue) <= 8, $"pixel {i}: {c} vs {picture[i]}");
		}
	}

	[Fact]
	public void Frame_delays_add_up_to_the_right_time()
	{
		int total = Enumerable.Range(0, 30).Sum(i => GifWriter.Delay(i, 30));
		Assert.Equal(100, total);                    // 30 frames at 30 fps: one second
		Assert.Equal(2, GifWriter.Delay(0, 100));    // never faster than players can show
	}

	// ======================= sprite sheets =======================

	[Fact]
	public void Sheet_cells_go_a_row_per_animation_or_in_a_grid()
	{
		var (cells, width, height) = SpriteSheetLayout.Arrange(new[] { 3, 2 }, 10, 8, columns: 0, rowPerAnimation: true, padding: 2);
		Assert.Equal(new[] { (2, 2), (14, 2), (26, 2) }, cells[0]);
		Assert.Equal(new[] { (2, 12), (14, 12) }, cells[1]);
		Assert.Equal((38, 22), (width, height));

		(cells, width, height) = SpriteSheetLayout.Arrange(new[] { 3, 2 }, 10, 8, columns: 2, rowPerAnimation: false, padding: 2);
		Assert.Equal(new[] { (2, 2), (14, 2), (2, 12) }, cells[0]);
		Assert.Equal(new[] { (14, 12), (2, 22) }, cells[1]);
		Assert.Equal((26, 32), (width, height));
	}

	[Fact]
	public void Sheet_data_reads_both_layouts()
	{
		var sheet = new SpriteSheetJson();
		sheet.Frames.Add(new SheetFrame { Filename = "walk 0", Frame = new SheetRect { X = 2, Y = 2, W = 10, H = 8 }, Duration = 50 });
		sheet.Meta.FrameTags.Add(new SheetTag { Name = "walk", From = 0, To = 0, Loop = true });
		sheet.Meta.Origin = new SheetPoint { X = 5, Y = 7.5f };

		var back = SpriteSheetJson.Read(sheet.ToJson());
		Assert.Equal("walk 0", Assert.Single(back.Frames).Filename);
		Assert.Equal(10, back.Frames[0].Frame.W);
		Assert.Equal("walk", Assert.Single(back.Meta.FrameTags).Name);
		Assert.Equal(7.5f, back.Meta.Origin!.Y);

		// The "hash" layout other tools write: frames keyed by name
		var hash = SpriteSheetJson.Read("""
			{ "frames": { "run_1.png": { "frame": { "x": 0, "y": 0, "w": 16, "h": 16 }, "duration": 80 } },
			  "meta": { "image": "run.png", "size": { "w": 16, "h": 16 } } }
			""");
		Assert.Equal("run_1.png", Assert.Single(hash.Frames).Filename);
		Assert.Equal(80, hash.Frames[0].Duration);
		Assert.Equal("run.png", hash.Meta.Image);
	}

	// ======================= exporting =======================

	// A drawn object with one 4 × 4 red part centred on its origin, and a 2-second loop
	private static (Scene Scene, SceneObject Obj, AssetStore Store) RedSquare()
	{
		var store = new AssetStore();
		var scene = new Scene { StageWidth = 10, StageHeight = 6 };
		var obj = SceneOps.AddDrawn(scene, "square", Vector2.Zero);
		var asset = new Asset { Name = "square", Width = 4, Height = 4 };
		using (var red = new SKBitmap(AssetStore.InfoFor(4, 4)))
		{
			red.Erase(new SKColor(220, 30, 30));
			store.AddLayer(asset, "Base", red);
		}
		PartOps.CreatePart(obj.Skeleton, obj.Skeleton.Roots[0], asset, "square", Vector2.Zero, 0f);
		obj.Animations[0].Duration = 2f;
		obj.Animations[0].Loop = true;
		return (scene, obj, store);
	}

	private static string TempFolder()
	{
		string folder = Path.Combine(Path.GetTempPath(), "harlequin-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		return folder;
	}

	[Fact]
	public void An_objects_animation_becomes_a_sprite_sheet_with_its_data()
	{
		var (scene, obj, store) = RedSquare();
		string folder = TempFolder();
		try
		{
			var settings = new ExportSettings
			{
				Scene = false, Object = obj, Animations = { obj.Animations[0] },
				Format = ExportFormat.SpriteSheet, Path = Path.Combine(folder, "hero.png"), Padding = 1
			};
			var exporter = new Exporter(scene, store, fps: 2, settings);
			exporter.Run().ToList();

			Assert.Equal(4, exporter.FrameCount);   // a 2-second loop at 2 fps (its end is its start)
			Assert.Equal((4, 4), exporter.Size);    // just the square
			using var sheet = SKBitmap.Decode(Path.Combine(folder, "hero.png"));
			Assert.Equal(1 + 4 * 5, sheet.Width);   // 4 cells of 4 px with 1 px gaps
			Assert.Equal(new SKColor(220, 30, 30), sheet.GetPixel(2, 2));
			Assert.Equal(0, sheet.GetPixel(0, 0).Alpha);

			var data = SpriteSheetJson.Read(File.ReadAllText(Path.Combine(folder, "hero.json")));
			Assert.Equal(4, data.Frames.Count);
			Assert.Equal(500, data.Frames[0].Duration);
			var tag = Assert.Single(data.Meta.FrameTags);
			Assert.Equal(("animation", 0, 3), (tag.Name, tag.From, tag.To));
			Assert.Equal(2f, data.Meta.Origin!.X);   // the origin is the square's middle
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	// Milestone 13: a blue square on top of the red one, until a key brings the red one to the
	// front at 1 second: the exported frames show the order the animation draws them in
	[Fact]
	public void Exported_frames_draw_the_parts_in_the_order_the_animation_keys()
	{
		var (scene, obj, store) = RedSquare();
		var blue = new SKColor(30, 30, 220);
		var asset = new Asset { Name = "blue", Width = 4, Height = 4 };
		using (var pixels = new SKBitmap(AssetStore.InfoFor(4, 4)))
		{
			pixels.Erase(blue);
			store.AddLayer(asset, "Base", pixels);
		}
		var skeleton = obj.Skeleton;
		PartOps.CreatePart(skeleton, skeleton.Roots[0], asset, "blue", Vector2.Zero, 0f);   // drawn on top
		var setup = skeleton.Slots.ToList();
		Assert.True(DrawOrderOps.Key(obj.Animations[0], 1f, DrawOrderOps.ToEnd(setup, new[] { skeleton.FindSlot("square")! }, front: true), setup));
		string folder = TempFolder();
		try
		{
			new Exporter(scene, store, fps: 2, new ExportSettings
			{
				Scene = false, Object = obj, Animations = { obj.Animations[0] },
				Format = ExportFormat.SpriteSheet, Path = Path.Combine(folder, "order.png"), Padding = 1
			}).Run().ToList();
			using var sheet = SKBitmap.Decode(Path.Combine(folder, "order.png"));
			var red = new SKColor(220, 30, 30);
			Assert.Equal(new[] { blue, blue, red, red }, Enumerable.Range(0, 4).Select(k => sheet.GetPixel(2 + 5 * k, 2)));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void The_scene_exports_its_stage_as_pictures_and_a_still_gif_is_one_frame()
	{
		var (scene, _, store) = RedSquare();
		scene.Duration = 1f;
		string folder = TempFolder();
		try
		{
			var pngs = new Exporter(scene, store, fps: 4, new ExportSettings
			{
				Scene = true, Format = ExportFormat.PngSequence, Path = Path.Combine(folder, "shot.png")
			});
			pngs.Run().ToList();
			Assert.Equal(4, Directory.GetFiles(folder, "shot_*.png").Length);
			using (var frame = SKBitmap.Decode(Path.Combine(folder, "shot_0000.png")))
			{
				Assert.Equal((10, 6), (frame.Width, frame.Height));   // the stage
				Assert.Equal(new SKColor(220, 30, 30), frame.GetPixel(5, 3));
				Assert.Equal(0, frame.GetPixel(0, 0).Alpha);
			}

			var gifs = new Exporter(scene, store, fps: 4, new ExportSettings
			{
				Scene = true, Format = ExportFormat.Gif, Path = Path.Combine(folder, "shot.gif"), Background = SKColors.White
			});
			gifs.Run().ToList();
			var bytes = File.ReadAllBytes(Path.Combine(folder, "shot.gif"));
			using var first = Decode(bytes, 0, out var codec);
			Assert.Equal(1, codec.FrameCount);                   // nothing moves: one frame…
			Assert.Equal(1000, codec.FrameInfo[0].Duration);     // …lasting the whole second
			Assert.Equal(SKColors.White, first.GetPixel(0, 0));
			codec.Dispose();
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void Stopping_an_export_removes_what_it_wrote()
	{
		var (scene, obj, store) = RedSquare();
		string folder = TempFolder();
		try
		{
			var exporter = new Exporter(scene, store, fps: 2, new ExportSettings
			{
				Scene = false, Object = obj, Animations = { obj.Animations[0] },
				Format = ExportFormat.PngSequence, Path = Path.Combine(folder, "hero.png")
			});
			using (var steps = exporter.Run().GetEnumerator())
				for (int i = 0; i < 3 && steps.MoveNext(); i++) { }   // a couple of frames, then stop

			Assert.Empty(Directory.GetFiles(folder));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}
}
