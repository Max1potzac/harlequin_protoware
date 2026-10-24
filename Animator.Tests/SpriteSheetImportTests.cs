using Animator.Core.Export;
using Animator.Viewer.Export;
using Animator.Viewer.Rendering;
using SkiaSharp;

namespace Animator.Tests;

// Milestone 10: cutting a sprite sheet into pieces, and bringing them in as objects or parts
public class SpriteSheetImportTests
{
	// ======================= cutting =======================

	// An opacity map drawn with text: '#' = visible, '.' = see-through
	private static (byte[] Alpha, int Width, int Height) Map(params string[] rows)
	{
		int w = rows[0].Length, h = rows.Length;
		var alpha = new byte[w * h];
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++)
				alpha[y * w + x] = rows[y][x] == '#' ? (byte)255 : (byte)0;
		return (alpha, w, h);
	}

	private static SKBitmap Blank(int width, int height)
	{
		var bitmap = new SKBitmap(AssetStore.InfoFor(width, height));
		bitmap.Erase(SKColors.Transparent);
		return bitmap;
	}

	[Fact]
	public void A_grid_skips_empty_cells_and_follows_the_offset_and_gaps()
	{
		var (alpha, w, h) = Map(
			"##....##.",
			"##....##.");

		// The picture's opacity, as the dialog reads it
		using (var picture = Blank(w, h))
		{
			for (int i = 0; i < alpha.Length; i++)
				if (alpha[i] != 0) picture.SetPixel(i % w, i / w, SKColors.White);
			Assert.Equal(alpha, SheetImporter.Alpha(picture));
		}

		Assert.Equal(3, SpriteSheetSlicer.Grid(w, h, 3, 2).Count);   // without the opacity: every cell
		Assert.Equal(new[] { new PixelRect(0, 0, 3, 2), new PixelRect(6, 0, 3, 2) },
			SpriteSheetSlicer.Grid(w, h, 3, 2, alpha: alpha));          // the middle cell is empty

		// One pixel in, one pixel between: cells at 1 and 5 (one at 9 would stick out)
		Assert.Equal(new[] { new PixelRect(1, 0, 3, 2), new PixelRect(5, 0, 3, 2) },
			SpriteSheetSlicer.Grid(w, h, 3, 2, offsetX: 1, gapX: 1));
	}

	[Fact]
	public void Finding_sprites_keeps_close_bits_together_and_drops_specks()
	{
		var (alpha, w, h) = Map(
			"....................",
			".####...............",
			".####.#.....####....",
			".####.......####....",
			".####.......####....",
			"............####....",
			"............####....",
			"....................",
			"..................#.",
			"....................");

		// The eye a pixel away from its body joins it; the speck is too small to keep
		Assert.Equal(new[] { new PixelRect(1, 1, 6, 4), new PixelRect(12, 2, 4, 5) },
			SpriteSheetSlicer.Find(alpha, w, h, gap: 2, smallest: 3));

		// Joining only what touches, and keeping everything: four sprites, in reading order
		Assert.Equal(new[]
			{
				new PixelRect(1, 1, 4, 4), new PixelRect(6, 2, 1, 1), new PixelRect(12, 2, 4, 5), new PixelRect(18, 8, 1, 1)
			},
			SpriteSheetSlicer.Find(alpha, w, h, gap: 0, smallest: 1));
	}

	[Fact]
	public void Pieces_read_in_rows_from_the_top_left()
	{
		var order = SpriteSheetSlicer.ReadingOrder(new[]
		{
			new PixelRect(20, 0, 5, 5), new PixelRect(10, 12, 5, 5), new PixelRect(0, 1, 5, 5), new PixelRect(0, 12, 5, 4)
		});
		Assert.Equal(new[]
		{
			new PixelRect(0, 1, 5, 5), new PixelRect(20, 0, 5, 5),   // level with each other: one row
			new PixelRect(0, 12, 5, 4), new PixelRect(10, 12, 5, 5)
		}, order);
	}

	[Fact]
	public void A_sheets_json_gives_the_pieces_animations_and_timing()
	{
		using var sheet = Blank(48, 16);
		var json = new SpriteSheetJson();
		for (int i = 0; i < 3; i++)
			json.Frames.Add(new SheetFrame
			{
				Filename = $"walk {i}.png", Frame = new SheetRect { X = i * 16, W = 16, H = 16 }, Duration = i == 1 ? 50 : 100
			});
		json.Frames.Add(new SheetFrame { Filename = "turned", Frame = new SheetRect { W = 16, H = 16 }, Rotated = true });
		json.Frames.Add(new SheetFrame { Filename = "outside", Frame = new SheetRect { X = 40, W = 16, H = 16 } });
		json.Meta.FrameTags.Add(new SheetTag { Name = "walk", From = 0, To = 2, Direction = "pingpong" });
		json.Meta.FrameTags.Add(new SheetTag { Name = "broken", From = 3, To = 4 });
		json.Meta.Origin = new SheetPoint { X = 8, Y = 15 };

		var plan = new SheetImportPlan(sheet, "hero");
		Assert.Equal(1, plan.ReadJson(json, fps: 20));    // the turned frame can't be used…
		Assert.Equal(3, plan.Pieces.Count);               // …nor the one sticking out of the sheet
		Assert.Equal("walk 0", plan.Pieces[0].Name);

		var walk = Assert.Single(plan.Animations);        // "broken" has no frames left
		Assert.Equal("walk", walk.Name);
		Assert.Equal(new[] { plan.Pieces[0], plan.Pieces[1], plan.Pieces[2], plan.Pieces[1] }, walk.Frames);   // there and back
		Assert.Equal(new[] { 2, 1, 2, 1 }, walk.Frames.Select(f => walk.HoldOf(f)));   // 100 ms = 2 frames at 20 fps
		Assert.True(walk.Loop);
		Assert.Equal(FrameAnchor.Origin, walk.Anchor);
		Assert.Equal(new Vector2(8, 15), plan.Origin!.Value);
		Assert.Empty(plan.StillPieces);
	}

	[Fact]
	public void Untagged_frames_named_alike_make_an_animation_in_the_order_of_their_numbers()
	{
		using var sheet = Blank(64, 16);
		var json = SpriteSheetJson.Read("""
			{ "frames": {
				"hero/walk_02.png": { "frame": { "x": 16, "y": 0, "w": 16, "h": 16 } },
				"hero/walk_01.png": { "frame": { "x": 0, "y": 0, "w": 16, "h": 16 } },
				"hero/walk_10.png": { "frame": { "x": 32, "y": 0, "w": 16, "h": 16 } },
				"logo.png": { "frame": { "x": 48, "y": 0, "w": 16, "h": 16 } } },
			  "meta": { "image": "hero.png" } }
			""");
		var plan = new SheetImportPlan(sheet, "hero");
		plan.ReadJson(json, fps: 10);

		var walk = Assert.Single(plan.Animations);
		Assert.Equal("hero_walk", walk.Name);
		Assert.Equal(new[] { "hero_walk_01", "hero_walk_02", "hero_walk_10" }, walk.Frames.Select(f => f.Name));
		Assert.Equal("logo", Assert.Single(plan.StillPieces).Name);   // no number: a still
	}

	[Fact]
	public void Frames_line_up_by_their_middle_their_bottom_or_the_sheets_origin()
	{
		var tall = new SheetPiece { Rect = new PixelRect(50, 60, 10, 20) };
		Assert.Equal(new Vector2(5, 10), SheetImporter.AnchorPixel(new SheetAnimation { Anchor = FrameAnchor.Centre }, tall, null));
		Assert.Equal(new Vector2(5, 20), SheetImporter.AnchorPixel(new SheetAnimation { Anchor = FrameAnchor.Bottom }, tall, null));
		Assert.Equal(new Vector2(3, 4),
			SheetImporter.AnchorPixel(new SheetAnimation { Anchor = FrameAnchor.Origin }, tall, new Vector2(3, 4)));

		// A frame trimmed down to its drawing measures from where it was in its whole frame
		var trimmed = new SheetPiece { Rect = new PixelRect(0, 0, 6, 9), Untrimmed = new PixelRect(2, 3, 16, 16) };
		Assert.Equal(new Vector2(6, 5), SheetImporter.AnchorPixel(new SheetAnimation(), trimmed, null));
		Assert.Equal(new Vector2(6, 13), SheetImporter.AnchorPixel(new SheetAnimation { Anchor = FrameAnchor.Bottom }, trimmed, null));
	}

	// ======================= bringing them in =======================

	// A 40 × 10 sheet of four 10 × 10 squares: red, green and blue make a walk (2 frames each),
	// the white one is a still
	private static SheetImportPlan Squares(SKBitmap sheet)
	{
		var colours = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.White };
		using (var canvas = new SKCanvas(sheet))
		{
			for (int i = 0; i < colours.Length; i++)
			{
				using var paint = new SKPaint { Color = colours[i] };
				canvas.DrawRect(i * 10, 0, 10, 10, paint);
			}
		}

		var plan = new SheetImportPlan(sheet, "sheet");
		for (int i = 0; i < 4; i++)
			plan.Pieces.Add(new SheetPiece { Rect = new PixelRect(i * 10, 0, 10, 10), Name = $"square{i + 1}" });
		var walk = new SheetAnimation { Name = "walk", Hold = 2 };
		foreach (var piece in plan.Pieces.Take(3))
		{
			piece.Animation = walk;
			walk.Frames.Add(piece);
		}
		plan.Animations.Add(walk);
		return plan;
	}

	private static SKColor ColourShown(AssetStore store, SceneObject obj, Slot slot)
	{
		var drawing = obj.Skeleton.ResolveAttachment(slot)!;
		return store.Get(obj.Skeleton.Assets[drawing.AssetId])!.GetPixel(5, 5);
	}

	[Fact]
	public void An_animation_and_a_still_come_in_as_drawn_objects_where_they_were_on_the_sheet()
	{
		using var sheet = Blank(40, 10);
		var plan = Squares(sheet);
		var store = new AssetStore();

		var made = SheetImporter.IntoScene(new Scene(), store, plan, new Vector2(100, 50), fps: 10);

		Assert.Equal(2, made.Count);
		var (walker, still) = (made[0], made[1]);
		Assert.Equal(("sheet", ObjectKind.Drawn), (walker.Name, walker.Kind));
		Assert.Equal(new Vector2(85, 50), walker.Position);    // its first frame's middle (the sheet's middle at 100, 50)
		Assert.Equal(new Vector2(115, 50), still.Position);

		// The walk: one part with three drawings, each shown for 2 frames
		var part = Assert.Single(walker.Skeleton.Slots);
		Assert.Equal(3, walker.Skeleton.DefaultSkin.AttachmentsFor(part.Name).Count());
		var animation = Assert.Single(walker.Animations);
		Assert.Equal(("walk", 0.6f), (animation.Name, animation.Duration));
		Assert.Equal(new[] { 0f, 0.2f, 0.4f }, animation.Find<AttachmentTimeline>(part.Name)!.Track.Keys.Select(k => k.Time));

		// A quarter of a second in, it shows the green square
		ScenePlayer.Pose(walker, 0.25f);
		Assert.Equal(SKColors.Lime, ColourShown(store, walker, part));

		// The still is an object of its own, with the white square
		ScenePlayer.Pose(still, 0f);
		Assert.Equal(SKColors.White, ColourShown(store, still, Assert.Single(still.Skeleton.Slots)));
	}

	[Fact]
	public void Animations_can_come_in_as_an_object_each_and_the_stills_can_stay_out()
	{
		using var sheet = Blank(40, 10);
		var plan = Squares(sheet);
		var blink = new SheetAnimation { Name = "blink" };
		plan.Pieces[3].Animation = blink;
		blink.Frames.Add(plan.Pieces[3]);
		plan.Animations.Add(blink);
		plan.Pieces.Add(new SheetPiece { Rect = new PixelRect(0, 0, 5, 5), Name = "corner" });   // a still
		plan.Pieces[2].LeftOut = true;   // a frame left out isn't brought in
		plan.AnimationsTogether = false;
		plan.Stills = false;

		var made = SheetImporter.IntoScene(new Scene(), new AssetStore(), plan, Vector2.Zero, fps: 10);

		Assert.Equal(new[] { "walk", "blink" }, made.Select(o => o.Name));
		var walker = made[0];
		Assert.Equal(2, walker.Skeleton.DefaultSkin.AttachmentsFor(walker.Skeleton.Slots[0].Name).Count());
		Assert.Equal(0.4f, walker.Animations[0].Duration);
	}

	[Fact]
	public void Pieces_become_parts_upright_on_a_bone_and_each_animation_a_new_animation()
	{
		using var sheet = Blank(40, 10);
		var plan = Squares(sheet);
		plan.Animations[0].Frames.Add(plan.Pieces[1]);   // back to the green square at the end
		var obj = SceneOps.AddRigged(new Scene(), "hero", Vector2.Zero);   // its bone points up
		var bone = obj.Skeleton.Roots[0];
		obj.Skeleton.SetToSetupPose();
		obj.Skeleton.UpdatePose();

		var made = SheetImporter.IntoObject(obj, bone, new AssetStore(), plan, fps: 10);

		Assert.Equal(2, made.Count);
		Assert.All(made, slot => Assert.Same(bone, slot.Bone));
		Assert.Equal(2, obj.Animations.Count);   // the object's own animation stays
		var walk = obj.Animations.Single(a => a.Name == "walk");
		Assert.Equal(0.8f, walk.Duration);
		Assert.Equal(4, walk.Find<AttachmentTimeline>(made[0].Name)!.Track.Keys.Count);
		Assert.Equal(3, obj.Skeleton.DefaultSkin.AttachmentsFor(made[0].Name).Count());   // the repeat shows the same drawing

		// The still sits where it was on the sheet (15 px right of its middle), upright on screen
		var still = obj.Skeleton.DefaultSkin.Get(made[1].Name, made[1].Name)!;
		var placed = Vector2.Transform(still.Offset, bone.World);
		Assert.Equal(15f, placed.X, 3);
		Assert.Equal(0f, placed.Y, 3);
		Assert.Equal(-MathF.PI / 2, still.Rotation, 3);
	}
}
