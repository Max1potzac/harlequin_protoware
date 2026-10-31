using System.Text.Json;
using Animator.Core.Serialization;
using Animator.Viewer.Rendering;
using SkiaSharp;

namespace Animator.Tests;

// Step 21: clipping masks. A part used as a mask clips the parts above it (all, or up to one):
// they show only where its picture does, and it isn't drawn unless it's set to show too
public class MaskTests
{
	// One bone at the origin; parts centred on it, back to front: a 10×10 mask, a 30×30 red
	// square, a 30×30 green square, then a 4×4 blue dot on top
	private static (Skeleton Skeleton, AssetStore Store, Slot Mask, Slot Red, Slot Green, Slot Dot) Scene()
	{
		var store = new AssetStore();
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root" };
		skeleton.Roots.Add(root);
		Slot Part(string name, int size, SKColor color)
		{
			var asset = new Asset { Name = name, Width = size, Height = size };
			using (var pixels = new SKBitmap(AssetStore.InfoFor(size, size)))
			{
				pixels.Erase(color);
				store.AddLayer(asset, "Base", pixels);
			}
			skeleton.Assets[asset.Id] = asset;
			skeleton.DefaultSkin.Set(name, new Attachment { Name = name, AssetId = asset.Id });
			var slot = new Slot(name, root) { SetupAttachment = name };
			skeleton.Slots.Add(slot);
			return slot;
		}
		var mask = Part("mask", 10, SKColors.White);
		var red = Part("red", 30, SKColors.Red);
		var green = Part("green", 30, SKColors.Lime);
		var dot = Part("dot", 4, SKColors.Blue);
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		return (skeleton, store, mask, red, green, dot);
	}

	// The object drawn with its origin at (50, 50) of a 100×100 picture
	private static SKBitmap Render(Skeleton skeleton, AssetStore store)
	{
		var bitmap = new SKBitmap(AssetStore.InfoFor(100, 100));
		using var canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.Transparent);
		canvas.Translate(50, 50);
		new SkeletonRenderer(store) { HideHidden = true }.DrawImages(canvas, skeleton);
		return bitmap;
	}

	[Fact]
	public void A_mask_clips_the_parts_above_it_to_its_picture_and_isnt_drawn()
	{
		var (skeleton, store, mask, red, green, dot) = Scene();
		MaskOps.MakeMask(skeleton, mask, until: green);
		Assert.Equal(new[] { red, green }, MaskOps.Clipped(skeleton, mask));
		Assert.Same(mask, MaskOps.MaskOf(skeleton, green));
		Assert.Null(MaskOps.MaskOf(skeleton, dot));

		using var picture = Render(skeleton, store);
		Assert.Equal(SKColors.Lime, picture.GetPixel(53, 47));          // inside the mask: the top clipped part
		Assert.Equal(0, picture.GetPixel(62, 50).Alpha);                // outside it: nothing (the squares are cut)
		Assert.Equal(SKColors.Blue, picture.GetPixel(50, 50));          // the dot: not clipped, on top
	}

	[Fact]
	public void A_mask_that_shows_is_drawn_under_what_it_clips_and_a_hidden_mask_clips_nothing()
	{
		var (skeleton, store, mask, red, green, _) = Scene();
		MaskOps.MakeMask(skeleton, mask, until: red, shows: true);
		using (var picture = Render(skeleton, store))
		{
			Assert.Equal(SKColors.Lime, picture.GetPixel(62, 50));   // green isn't clipped (red is the last)
			Assert.Equal(SKColors.Lime, picture.GetPixel(53, 47));
		}
		MaskOps.MakeMask(skeleton, mask, until: null, shows: true);
		green.Hidden = red.Hidden = true;
		using (var picture = Render(skeleton, store))
			Assert.Equal(SKColors.White, picture.GetPixel(53, 47));   // the mask itself shows

		mask.Hidden = true;   // its eye shut: it clips nothing
		green.Hidden = false;
		using var unclipped = Render(skeleton, store);
		Assert.Equal(SKColors.Lime, unclipped.GetPixel(62, 50));
	}

	[Fact]
	public void Clipped_parts_are_picked_only_where_they_show()
	{
		var (skeleton, store, mask, _, green, _) = Scene();
		MaskOps.MakeMask(skeleton, mask);
		var renderer = new SkeletonRenderer(store);
		Assert.Same(green, renderer.Pick(skeleton, new Vector2(3, 3))?.Slot);
		Assert.Null(renderer.Pick(skeleton, new Vector2(12, 0)));   // cut off there (and the mask isn't drawn)
	}

	[Fact]
	public void Masks_are_saved_undone_and_survive_their_last_part_going()
	{
		var (skeleton, _, mask, red, green, dot) = Scene();
		var before = StructureSnapshot.Capture(skeleton, new List<Animation>());
		MaskOps.MakeMask(skeleton, mask, until: green, shows: true);

		var dto = ProjectMapper.ToDto(skeleton, Array.Empty<Animation>(), 30);
		var (loaded, _) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(JsonSerializer.Serialize(dto, ProjectMapper.Json), ProjectMapper.Json)!);
		var m = loaded.FindSlot("mask")!;
		Assert.True(m.IsMask && m.MaskShows);
		Assert.Same(loaded.FindSlot("green"), m.MaskUntil);
		Assert.False(loaded.FindSlot("dot")!.IsMask);

		PartOps.DeletePart(skeleton, Array.Empty<Animation>(), green);   // its last part gone: it ends at the one below
		Assert.Same(red, mask.MaskUntil);
		Assert.Equal(new[] { red }, MaskOps.Clipped(skeleton, mask));

		before.Restore();
		Assert.False(mask.IsMask);
		Assert.Null(mask.MaskUntil);
		Assert.Contains(green, skeleton.Slots);
		Assert.Null(MaskOps.MaskOf(skeleton, dot));
	}
}
