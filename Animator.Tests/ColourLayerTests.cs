using System.Text.Json;
using Animator.Core.Serialization;
using Animator.Viewer.Rendering;
using SkiaSharp;
using Rig = Animator.Tests.PickingTests.Rig;

namespace Animator.Tests;

// Step 16, part 2: colour layers on whole objects and inside drawings, keyed, saved, and baked
// into exports only when asked
public class ColourLayerTests
{
	private static ColorAdjust Adjust(ColorAdjustKind kind, Func<AdjustValues, AdjustValues> set, bool colorize = false, bool keepLight = true)
	{
		var values = set(AdjustValues.Default);
		return new ColorAdjust(kind) { Colorize = colorize, KeepLight = keepLight, Setup = values, Now = values };
	}

	private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 0.01f) =>
		Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

	private static Vector3 Rgb(SKColor c) => new(c.Red / 255f, c.Green / 255f, c.Blue / 255f);

	// ======================= what they do to a colour =======================

	[Fact]
	public void Hue_saturation_and_lightness()
	{
		var red = new Vector3(1, 0, 0);
		var turned = ColorFilters.Apply(red, Adjust(ColorAdjustKind.HueSaturation, v => v with { Hue = 120 }), 1);
		Assert.True(turned.Y > turned.X && turned.Y > turned.Z, $"red turned 120° should be greenish, got {turned}");

		var colour = new Vector3(0.8f, 0.4f, 0.2f);
		var grey = ColorFilters.Apply(colour, Adjust(ColorAdjustKind.HueSaturation, v => v with { Saturation = -1 }), 1);
		float lum = ColorFilters.Luminance(colour);
		AssertNear(new Vector3(lum), grey);

		var blue = ColorFilters.Apply(new Vector3(0.5f), Adjust(ColorAdjustKind.HueSaturation, v => v with { Hue = 240 }, colorize: true), 1);
		Assert.True(blue.Z > blue.X && blue.Z > blue.Y, $"colorized blue, got {blue}");

		AssertNear(Vector3.One, ColorFilters.Apply(colour, Adjust(ColorAdjustKind.HueSaturation, v => v with { Lightness = 1 }), 1));
		AssertNear(Vector3.Zero, ColorFilters.Apply(colour, Adjust(ColorAdjustKind.HueSaturation, v => v with { Lightness = -1 }), 1));
	}

	[Fact]
	public void Brightness_contrast_tint_and_balance()
	{
		AssertNear(new Vector3(0.6f), ColorFilters.Apply(new Vector3(0.5f), Adjust(ColorAdjustKind.BrightnessContrast, v => v with { Brightness = 0.2f }), 1));
		var punchy = Adjust(ColorAdjustKind.BrightnessContrast, v => v with { Contrast = 1 });
		AssertNear(Vector3.One, ColorFilters.Apply(new Vector3(0.75f), punchy, 1));
		AssertNear(Vector3.Zero, ColorFilters.Apply(new Vector3(0.25f), punchy, 1));

		var flash = Adjust(ColorAdjustKind.Tint, v => v with { TintColor = new Vector3(1, 0, 0), TintAmount = 1 }, keepLight: false);
		AssertNear(new Vector3(1, 0, 0), ColorFilters.Apply(new Vector3(0.2f, 0.7f, 0.4f), flash, 1));
		AssertNear(new Vector3(0.6f, 0.35f, 0.2f), ColorFilters.Apply(new Vector3(0.2f, 0.7f, 0.4f), flash, 0.5f));   // half as strong

		var warm = Adjust(ColorAdjustKind.Tint, v => v with { TintColor = new Vector3(1, 0.6f, 0.2f), TintAmount = 1 });
		var lit = ColorFilters.Apply(new Vector3(0.5f), warm, 1);
		Assert.True(lit.X > 0.5f && lit.Z < 0.5f, $"a warm filter on grey, got {lit}");

		var redShadows = Adjust(ColorAdjustKind.ColorBalance, v => v with { Shadows = new Vector3(1, 0, 0) });
		AssertNear(new Vector3(0.5f, 0, 0), ColorFilters.Apply(Vector3.Zero, redShadows, 1));
		AssertNear(Vector3.One, ColorFilters.Apply(Vector3.One, redShadows, 1));   // highlights left alone
	}

	[Fact]
	public void Replace_colour_changes_the_colours_near_the_one_picked_and_keeps_their_shading()
	{
		var replace = Adjust(ColorAdjustKind.ReplaceColor, v => v with
		{
			From = new Vector3(1, 0, 0), To = new Vector3(0, 1, 0), Tolerance = 0.1f, Softness = 0.1f
		});
		AssertNear(new Vector3(0, 1, 0), ColorFilters.Apply(new Vector3(1, 0, 0), replace, 1));
		AssertNear(new Vector3(0, 1, 0.05f), ColorFilters.Apply(new Vector3(0.95f, 0, 0.05f), replace, 1));   // a little off: still in, shifted alike
		AssertNear(new Vector3(0, 0, 1), ColorFilters.Apply(new Vector3(0, 0, 1), replace, 1));                 // far off: left alone
	}

	[Theory]
	[InlineData(ColorAdjustKind.HueSaturation)]
	[InlineData(ColorAdjustKind.BrightnessContrast)]
	[InlineData(ColorAdjustKind.Tint)]
	[InlineData(ColorAdjustKind.ColorBalance)]
	public void The_drawing_filter_does_what_the_sums_say(ColorAdjustKind kind)
	{
		var adjust = Adjust(kind, v => v with
		{
			Hue = 70, Saturation = 0.4f, Lightness = -0.2f, Brightness = 0.1f, Contrast = 0.3f,
			TintColor = new Vector3(0.2f, 0.5f, 1f), TintAmount = 0.6f,
			Shadows = new Vector3(0.3f, -0.2f, 0.1f), Midtones = new Vector3(-0.1f, 0.4f, 0f), Highlights = new Vector3(0f, 0f, -0.5f),
			Mix = 0.8f
		});
		using var filter = ColorFilters.For(adjust, 1);
		Assert.NotNull(filter);
		foreach (var colour in new[] { new SKColor(200, 60, 30), new SKColor(20, 140, 220), new SKColor(128, 128, 128) })
		{
			using var source = new SKBitmap(AssetStore.InfoFor(1, 1));
			source.Erase(colour);
			using var target = new SKBitmap(AssetStore.InfoFor(1, 1));
			target.Erase(SKColors.Transparent);
			using (var canvas = new SKCanvas(target))
			using (var paint = new SKPaint { ColorFilter = filter })
				canvas.DrawBitmap(source, 0, 0, paint);
			AssertNear(ColorFilters.Apply(Rgb(colour), adjust, 1), Rgb(target.GetPixel(0, 0)), 3 / 255f);
		}
	}

	// ======================= inside a drawing =======================

	[Fact]
	public void A_drawings_colour_layer_changes_the_layers_under_it_and_can_be_left_out_of_an_export()
	{
		var store = new AssetStore();
		var asset = new Asset { Name = "gem", Width = 2, Height = 1 };
		using (var red = new SKBitmap(AssetStore.InfoFor(2, 1)))
		{
			red.Erase(SKColors.Red);
			store.AddLayer(asset, "Base", red);
		}
		var colour = store.AddLayer(asset, "Colour");
		colour.Adjust = Adjust(ColorAdjustKind.ReplaceColor, v => v with { From = new Vector3(1, 0, 0), To = new Vector3(0, 0, 1) });
		using (var green = new SKBitmap(AssetStore.InfoFor(2, 1)))
		{
			green.Erase(SKColors.Transparent);
			green.SetPixel(1, 0, SKColors.Lime);
			store.AddLayer(asset, "On top", green);   // above the colour layer: not changed
		}
		asset.MarkEdited();

		var picture = store.Get(asset)!;
		Assert.Equal(SKColors.Blue, picture.GetPixel(0, 0));
		Assert.Equal(SKColors.Lime, picture.GetPixel(1, 0));
		Assert.Equal(SKColors.Red, store.Get(asset, colorLayers: false)!.GetPixel(0, 0));

		colour.Visible = false;
		asset.MarkEdited();
		Assert.Equal(SKColors.Red, store.Get(asset)!.GetPixel(0, 0));
	}

	// ======================= on a whole object =======================

	private static SKColor PixelAt(Rig rig, SkeletonRenderer renderer, float x, float y)
	{
		using var bitmap = new SKBitmap(400, 400);
		using (var canvas = new SKCanvas(bitmap))
		{
			canvas.Clear(SKColors.Transparent);
			canvas.Translate(200, 200);
			canvas.Scale(1, -1);   // y up, as in the viewport
			renderer.DrawImages(canvas, rig.Skeleton);
		}
		var p = Rig.Screen(x, y);
		return bitmap.GetPixel((int)p.X, (int)p.Y);
	}

	[Fact]
	public void An_objects_colour_layer_changes_every_part_but_the_ones_left_out()
	{
		var rig = new Rig();
		var darker = new ColorLayer { Name = "darker", Adjust = Adjust(ColorAdjustKind.BrightnessContrast, v => v with { Brightness = -1 }) };
		darker.LeftOut.Add(rig.Hand);
		rig.Skeleton.ColorLayers.Add(darker);

		var renderer = new SkeletonRenderer(rig.Store);
		Assert.Equal(new SKColor(128, 128, 128), PixelAt(rig, renderer, 0, -100));   // the body: white made darker
		Assert.Equal(SKColors.White, PixelAt(rig, renderer, 60, 50));                // the hand: left out

		renderer.ColorLayers = false;   // an export that doesn't bake them in
		Assert.Equal(SKColors.White, PixelAt(rig, renderer, 0, -100));
		renderer.ColorLayers = true;
		darker.Visible = false;
		Assert.Equal(SKColors.White, PixelAt(rig, renderer, 0, -100));
	}

	[Fact]
	public void Keyed_colour_layers_blend_from_key_to_key_and_go_back_to_rest()
	{
		var rig = new Rig();
		var gem = new ColorLayer { Name = "gem", Adjust = Adjust(ColorAdjustKind.HueSaturation, v => v) };
		rig.Skeleton.ColorLayers.Add(gem);
		var keys = new ColorLayerTimeline("gem");
		keys.Track.Add(0f, AdjustValues.Default with { Hue = -60 });
		keys.Track.Add(1f, AdjustValues.Default with { Hue = 60 });
		rig.Animation.Timelines.Add(keys);

		rig.Skeleton.SetToSetupPose();
		rig.Animation.Apply(rig.Skeleton, 0.5f);
		Assert.Equal(0f, gem.Adjust.Now.Hue, 3);
		rig.Animation.Apply(rig.Skeleton, 0.75f);
		Assert.Equal(30f, gem.Adjust.Now.Hue, 3);
		rig.Skeleton.SetToSetupPose();
		Assert.Equal(gem.Adjust.Setup, gem.Adjust.Now);
	}

	[Fact]
	public void With_auto_key_off_a_colour_change_is_held_until_it_is_keyed()
	{
		var rig = new Rig();
		var gem = new ColorLayer { Name = "gem", Adjust = Adjust(ColorAdjustKind.HueSaturation, v => v) };
		rig.Skeleton.ColorLayers.Add(gem);
		var hold = new PoseHold();
		var shifted = AdjustValues.Default with { Hue = 45 };
		hold.HoldColor(gem, shifted, rig.Skeleton, rig.Animation, 3);

		rig.Skeleton.SetToSetupPose();
		hold.Apply(rig.Skeleton, rig.Animation, 3);
		Assert.Equal(45f, gem.Adjust.Now.Hue);
		Assert.True(hold.Holds(gem));

		hold.KeyInto(rig.Animation, 3 / 30f);
		Assert.Equal(shifted, Assert.Single(rig.Animation.Find<ColorLayerTimeline>("gem")!.Track.Keys).Value);
	}

	[Fact]
	public void Renaming_a_colour_layer_takes_its_keys_along_and_undo_puts_everything_back()
	{
		var rig = new Rig();
		var gem = new ColorLayer { Name = "gem" };
		rig.Skeleton.ColorLayers.Add(gem);
		rig.Animation.Timelines.Add(new ColorLayerTimeline("gem"));
		var before = ColorLayersSnapshot.Capture(rig.Skeleton);

		Assert.True(ColorLayerOps.Rename(rig.Skeleton, new[] { rig.Animation }, gem, "glow"));
		Assert.NotNull(rig.Animation.Find<ColorLayerTimeline>("glow"));
		rig.Skeleton.ColorLayers.Add(new ColorLayer { Name = "other" });
		Assert.False(ColorLayerOps.Rename(rig.Skeleton, new[] { rig.Animation }, gem, "Other"));   // taken
		gem.LeftOut.Add(rig.Body);

		before.Restore();
		Assert.Same(gem, Assert.Single(rig.Skeleton.ColorLayers));
		Assert.Equal("gem", gem.Name);
		Assert.Empty(gem.LeftOut);
	}

	// ======================= the file =======================

	[Fact]
	public void Colour_layers_and_their_keys_are_saved_and_loaded_and_copies_keep_theirs()
	{
		var rig = new Rig();
		var tint = new ColorLayer
		{
			Name = "night",
			Visible = false,
			Adjust = Adjust(ColorAdjustKind.Tint, v => v with { TintColor = new Vector3(0.1f, 0.2f, 0.9f), TintAmount = 0.4f }, keepLight: false)
		};
		tint.LeftOut.Add(rig.Hand);
		rig.Skeleton.ColorLayers.Add(tint);
		var nightKeys = new ColorLayerTimeline("night");
		nightKeys.Track.Add(0.5f, AdjustValues.Default with { TintAmount = 0.9f, Mix = 0.5f });
		rig.Animation.Timelines.Add(nightKeys);

		var asset = rig.Skeleton.Assets.Values.First(a => a.Name == "body");
		var shine = rig.Store.AddLayer(asset, "shine");
		shine.Adjust = Adjust(ColorAdjustKind.HueSaturation, v => v with { Hue = 33 }, colorize: true);
		var shineKeys = new LayerAdjustTimeline(LayerAdjustTimeline.TargetOf(shine)) { Caption = "body: shine" };
		shineKeys.Track.Add(0.25f, AdjustValues.Default with { Hue = 90 });
		rig.Animation.Timelines.Add(shineKeys);

		var scene = new Scene { Fps = 30 };
		var character = new SceneObject("Character", ObjectKind.Rigged, rig.Skeleton);
		character.Animations.Add(rig.Animation);
		scene.Objects.Add(character);
		var loaded = ProjectMapper.SceneFromDto(JsonSerializer.Deserialize<ProjectDto>(
			JsonSerializer.Serialize(ProjectMapper.SceneToDto(scene), ProjectMapper.Json), ProjectMapper.Json)!).Objects[0];

		var night = Assert.Single(loaded.Skeleton.ColorLayers);
		Assert.Equal("night", night.Name);
		Assert.False(night.Visible);
		Assert.False(night.Adjust.KeepLight);
		Assert.Equal(tint.Adjust.Setup, night.Adjust.Setup);
		Assert.Equal(new[] { "hand" }, night.LeftOut.Select(s => s.Name));
		var anim = loaded.Animations.Single();
		Assert.Equal(nightKeys.Track.Keys[0].Value, Assert.Single(anim.Find<ColorLayerTimeline>("night")!.Track.Keys).Value);

		var loadedShine = loaded.Skeleton.Assets[asset.Id].Layers.Single(l => l.Name == "shine");
		Assert.True(loadedShine.Adjust!.Colorize);
		Assert.Equal(33f, loadedShine.Adjust.Setup.Hue);
		var loadedShineKeys = anim.Timelines.OfType<LayerAdjustTimeline>().Single();
		Assert.Equal(LayerAdjustTimeline.TargetOf(loadedShine), loadedShineKeys.Target);
		Assert.Equal("body: shine", loadedShineKeys.Caption);

		// A copy of the object: its drawings' layers are new ones, and the keys follow them
		var (copy, _) = ProjectMapper.CloneObject(character, "Copy");
		var copiedShine = copy.Skeleton.Assets.Values.SelectMany(a => a.Layers).Single(l => l.Name == "shine");
		Assert.NotEqual(shine.Id, copiedShine.Id);
		Assert.Equal(LayerAdjustTimeline.TargetOf(copiedShine), copy.Animations.Single().Timelines.OfType<LayerAdjustTimeline>().Single().Target);
	}

	[Fact]
	public void Missing_numbers_read_as_the_defaults()
	{
		var values = AdjustValues.FromArray(new[] { 0.5f, 10f });
		Assert.Equal(0.5f, values.Mix);
		Assert.Equal(10f, values.Hue);
		Assert.Equal(AdjustValues.Default.TintColor, values.TintColor);
		Assert.Equal(AdjustValues.Default, AdjustValues.FromArray(AdjustValues.Default.ToArray()));
	}
}
