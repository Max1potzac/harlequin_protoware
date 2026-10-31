using System.Text.Json;
using Animator.Core.Serialization;
using Animator.Viewer.Rendering;
using SkiaSharp;

namespace Animator.Tests;

// Effects (milestone 9.4): blend modes, tint and opacity, and their keys
public class EffectTests
{
	private static (Skeleton Skeleton, Slot Slot, Animation Animation) Rig()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 10 };
		skeleton.Roots.Add(root);
		var slot = new Slot("flash", root) { SetupAttachment = "flash" };
		skeleton.Slots.Add(slot);
		return (skeleton, slot, new Animation { Name = "shoot", Duration = 1f });
	}

	[Fact]
	public void Colour_and_opacity_keys_animate_and_rest_values_come_back()
	{
		var (skeleton, slot, animation) = Rig();
		slot.SetupColor = new Vector3(0.25f, 0.5f, 0.75f);
		slot.SetupOpacity = 0.8f;
		var color = new PartColorTimeline("flash");
		color.Track.Add(0f, new Vector3(1, 1, 1));
		color.Track.Add(1f, new Vector3(1, 0, 0));
		var fade = new PartOpacityTimeline("flash");
		fade.Track.Add(0f, 1f);
		fade.Track.Add(1f, 0f);

		IkMatching.Evaluate(skeleton, animation, 0.5f);   // no keys yet: the rest values
		Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), slot.Color);
		Assert.Equal(0.8f, slot.Opacity);

		animation.Timelines.Add(color);
		animation.Timelines.Add(fade);
		IkMatching.Evaluate(skeleton, animation, 0.5f);
		Assert.Equal(new Vector3(1f, 0.5f, 0.5f), slot.Color);   // halfway from white to red
		Assert.Equal(0.5f, slot.Opacity);
	}

	[Fact]
	public void Effect_keys_follow_a_renamed_part_and_go_with_a_deleted_one()
	{
		var (skeleton, slot, animation) = Rig();
		animation.Timelines.Add(new PartColorTimeline("flash"));
		animation.Timelines.Add(new PartOpacityTimeline("flash"));

		Assert.True(PartOps.RenamePart(skeleton, new[] { animation }, slot, "glow"));
		Assert.All(animation.Timelines, t => Assert.Equal("glow", t.Target));

		PartOps.DeletePart(skeleton, new[] { animation }, slot);
		Assert.Empty(animation.Timelines);
	}

	[Fact]
	public void Blend_colour_opacity_and_their_keys_are_saved_and_loaded()
	{
		var (skeleton, slot, animation) = Rig();
		slot.Blend = BlendMode.Add;
		slot.SetupColor = new Vector3(1f, 0.75f, 0.25f);
		slot.SetupOpacity = 0.5f;
		var plain = new Slot("plain", slot.Bone);
		skeleton.Slots.Add(plain);
		var color = new PartColorTimeline("flash");
		color.Track.Add(0.25f, new Vector3(0.1f, 0.2f, 0.3f), CurveType.Smooth);
		var fade = new PartOpacityTimeline("flash");
		fade.Track.Add(0.5f, 0.25f, CurveType.Stepped);
		animation.Timelines.Add(color);
		animation.Timelines.Add(fade);

		var dto = ProjectMapper.ToDto(skeleton, new[] { animation }, 30);
		string json = JsonSerializer.Serialize(dto, ProjectMapper.Json);
		var (loadedSkeleton, loaded) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(json, ProjectMapper.Json)!);

		var flash = loadedSkeleton.FindSlot("flash")!;
		Assert.Equal(BlendMode.Add, flash.Blend);
		Assert.Equal(new Vector3(1f, 0.75f, 0.25f), flash.SetupColor);
		Assert.Equal(0.5f, flash.SetupOpacity);

		var plainBack = loadedSkeleton.FindSlot("plain")!;   // nothing special: nothing extra in the file
		Assert.Equal(BlendMode.Normal, plainBack.Blend);
		Assert.Equal(Vector3.One, plainBack.SetupColor);
		Assert.Equal(1f, plainBack.SetupOpacity);
		var plainDto = dto.Slots.Single(s => s.Name == "plain");
		Assert.Null(plainDto.Blend);
		Assert.Null(plainDto.Color);
		Assert.Null(plainDto.Opacity);

		var colorBack = Assert.IsType<PartColorTimeline>(loaded[0].Timelines[0]);
		Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), colorBack.Track.Keys[0].Value);
		Assert.Equal(CurveType.Smooth, colorBack.Track.Keys[0].Curve);
		var fadeBack = Assert.IsType<PartOpacityTimeline>(loaded[0].Timelines[1]);
		Assert.Equal(0.25f, fadeBack.Track.Keys[0].Value);
		Assert.Equal(CurveType.Stepped, fadeBack.Track.Keys[0].Curve);
	}

	// Draws one 8×8 part in `paint` over `background`, and reads the pixel in its middle
	private static SKColor Render(SKColor background, SKColor paint, BlendMode blend,
								  Vector3? tint = null, float opacity = 1f)
	{
		var store = new AssetStore();
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 10 };
		skeleton.Roots.Add(root);

		var asset = new Asset { Name = "fx", Width = 8, Height = 8 };
		using (var pixels = new SKBitmap(AssetStore.InfoFor(8, 8)))
		{
			pixels.Erase(paint);
			store.AddLayer(asset, "Base", pixels);
		}
		skeleton.Assets[asset.Id] = asset;
		skeleton.DefaultSkin.Set("fx", new Attachment { Name = "fx", AssetId = asset.Id });
		skeleton.Slots.Add(new Slot("fx", root)
		{
			SetupAttachment = "fx", Blend = blend, SetupColor = tint ?? Vector3.One, SetupOpacity = opacity
		});
		skeleton.SetToSetupPose();
		skeleton.UpdatePose();

		var renderer = new SkeletonRenderer(store) { PixelArt = true };
		using var target = new SKBitmap(AssetStore.InfoFor(16, 16));
		using (var canvas = new SKCanvas(target))
		{
			canvas.Clear(background);
			canvas.Translate(8, 8);   // the part is centred on the bone, which sits at the origin
			renderer.DrawImages(canvas, skeleton);
		}
		return target.GetPixel(8, 8);
	}

	private static void AssertColor(SKColor expected, SKColor actual)
	{
		Assert.InRange(actual.Red, expected.Red - 2, expected.Red + 2);
		Assert.InRange(actual.Green, expected.Green - 2, expected.Green + 2);
		Assert.InRange(actual.Blue, expected.Blue - 2, expected.Blue + 2);
	}

	[Fact]
	public void Blend_modes_mix_with_what_is_behind()
	{
		AssertColor(new SKColor(0, 100, 0), Render(new SKColor(100, 0, 0), new SKColor(0, 100, 0), BlendMode.Normal));
		AssertColor(new SKColor(100, 100, 0), Render(new SKColor(100, 0, 0), new SKColor(0, 100, 0), BlendMode.Add));
		AssertColor(new SKColor(100, 100, 100), Render(new SKColor(200, 200, 200), new SKColor(128, 128, 128), BlendMode.Multiply));
		AssertColor(new SKColor(161, 161, 161), Render(new SKColor(100, 100, 100), new SKColor(100, 100, 100), BlendMode.Screen));
	}

	[Fact]
	public void Tint_multiplies_colours_and_opacity_fades()
	{
		AssertColor(new SKColor(255, 128, 0), Render(SKColors.Black, SKColors.White, BlendMode.Normal, new Vector3(1f, 0.5f, 0f)));
		AssertColor(new SKColor(100, 50, 25), Render(SKColors.Black, new SKColor(200, 100, 50), BlendMode.Normal, opacity: 0.5f));
	}
}
