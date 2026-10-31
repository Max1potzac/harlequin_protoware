using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Step 19: skins. Several parts in one skin, categories of alternatives (one on at a time),
// overlays that stack, and existing parts or drawings made into skins.
public class SkinTests
{
	private const int Fps = 30;

	// A zombie: a head, a bucket and its two dents (three layers), a bandage
	private static Skeleton Zombie()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root" };
		skeleton.Roots.Add(root);
		foreach (var name in new[] { "head", "bucket", "bucket_dented", "bucket_wrecked", "bandage" })
			Part(skeleton, root, name);
		return skeleton;
	}

	private static Slot Part(Skeleton skeleton, Bone bone, string name, params string[] moreDrawings)
	{
		var slot = new Slot(name, bone) { SetupAttachment = name, ActiveAttachment = name };
		skeleton.Slots.Add(slot);
		foreach (var drawing in moreDrawings.Prepend(name))
		{
			var asset = new Asset { Name = drawing, Width = 4, Height = 4 };
			skeleton.Assets[asset.Id] = asset;
			skeleton.DefaultSkin.Set(name, new Attachment { Name = drawing, AssetId = asset.Id });
		}
		return slot;
	}

	private static Slot S(Skeleton skeleton, string name) => skeleton.FindSlot(name)!;
	private static Guid PixelsOf(Skeleton skeleton, string part) => skeleton.ResolveAttachment(S(skeleton, part))!.AssetId;

	[Fact]
	public void Skins_of_a_category_and_outfits_are_on_one_at_a_time_and_overlays_stack()
	{
		var skeleton = new Skeleton();
		var bucket = new Skin { Name = "Bucket", Category = "Bucket" };
		var dented = new Skin { Name = "Dented", Category = "bucket" };   // (the same category, whatever the case)
		var cone = new Skin { Name = "Cone", Category = "Cone" };
		var suit = new Skin { Name = "Suit" };
		var tux = new Skin { Name = "Tux" };
		var bandage = new Skin { Name = "Bandage", Overlay = true };
		var dirt = new Skin { Name = "Dirt", Category = "Bucket", Overlay = true };
		skeleton.Skins.AddRange(new[] { bucket, dented, cone, suit, tux, bandage, dirt });

		foreach (var skin in new[] { bucket, cone, suit, bandage, dirt }) SkinOps.Show(skeleton, skin);
		Assert.Equal(new[] { bucket, cone, suit, bandage, dirt }.ToHashSet(), skeleton.ShownSkins);

		SkinOps.Show(skeleton, dented);   // the bucket's other state: the bucket goes off, the overlays stay
		SkinOps.Show(skeleton, tux);      // another outfit
		Assert.Equal(new[] { dented, cone, tux, bandage, dirt }.ToHashSet(), skeleton.ShownSkins);

		SkinOps.SetKind(skeleton, cone, "Bucket", overlay: false);   // moved into the bucket's category: it wins there
		Assert.DoesNotContain(dented, skeleton.ShownSkins);
		Assert.Contains(cone, skeleton.ShownSkins);
		Assert.Equal(new[] { "Bucket" }, SkinOps.Categories(skeleton));   // (no one left in Cone)

		SkinOps.Show(skeleton, bandage, on: false);
		Assert.DoesNotContain(bandage, skeleton.ShownSkins);
	}

	[Fact]
	public void A_skins_drawings_show_while_it_is_on_overlays_winning_and_parts_only_in_skins_hide_otherwise()
	{
		var skeleton = Zombie();
		var head = S(skeleton, "head");
		var own = PixelsOf(skeleton, "head");
		var bucket = SkinOps.MakeSkin(skeleton, "Bucket", new[] { S(skeleton, "bucket") });
		Assert.NotNull(skeleton.ResolveAttachment(S(skeleton, "bucket")));   // made on

		SkinOps.Show(skeleton, bucket, on: false);
		Assert.Null(skeleton.ResolveAttachment(S(skeleton, "bucket")));   // only in the skin: not drawn
		Assert.Equal(own, PixelsOf(skeleton, "head"));                     // the rest as it is

		// Two skins with their own head: the overlay wins, else the later one
		var pale = new Skin { Name = "Pale" };
		var paleHead = new Attachment { Name = "head", AssetId = Guid.NewGuid() };
		pale.Set("head", paleHead);
		var mask = new Skin { Name = "Mask", Overlay = true };
		var masked = new Attachment { Name = "head", AssetId = Guid.NewGuid() };
		mask.Set("head", masked);
		skeleton.Skins.Insert(0, mask);   // (earlier in the list, but an overlay)
		skeleton.Skins.Add(pale);
		SkinOps.Show(skeleton, pale);
		Assert.Same(paleHead, skeleton.ResolveAttachment(head));
		SkinOps.Show(skeleton, mask);
		Assert.Same(masked, skeleton.ResolveAttachment(head));
		Assert.Same(masked, DrawingOps.Find(skeleton, head, "head"));
	}

	[Fact]
	public void Picked_parts_become_one_skin_and_keep_their_pixels_while_it_is_off_and_undo_brings_them_back()
	{
		var skeleton = Zombie();
		var parts = new[] { S(skeleton, "bucket"), S(skeleton, "bandage") };
		var pixels = parts.Select(p => PixelsOf(skeleton, p.Name)).ToList();
		var before = StructureSnapshot.Capture(skeleton, new List<Animation>());

		var skin = SkinOps.MakeSkin(skeleton, "Bucket", parts);
		Assert.Equal(parts, SkinOps.PartsOf(skeleton, skin));
		Assert.False(SkinOps.HasOwnDrawings(skeleton, parts[0]));
		Assert.Equal(new[] { skin }, SkinOps.SkinsWith(skeleton, parts[1]));
		SkinOps.Show(skeleton, skin, on: false);
		PartOps.RemoveUnusedAssets(skeleton);
		Assert.All(pixels, id => Assert.True(skeleton.Assets.ContainsKey(id)));   // not dropped: the skin still has them

		before.Restore();
		Assert.Empty(skeleton.Skins);
		Assert.Empty(skeleton.ShownSkins);
		Assert.Equal(pixels, parts.Select(p => PixelsOf(skeleton, p.Name)));
		Assert.True(SkinOps.HasOwnDrawings(skeleton, parts[0]));
	}

	[Fact]
	public void Picked_layers_become_a_category_one_skin_each_and_one_on()
	{
		var skeleton = Zombie();
		var layers = new[] { "bucket", "bucket_dented", "bucket_wrecked" }.Select(n => S(skeleton, n)).ToList();
		var made = SkinOps.MakeCategoryFromParts(skeleton, "Bucket", layers);

		Assert.Equal(new[] { "bucket", "bucket_dented", "bucket_wrecked" }, made.Select(s => s.Name));
		Assert.All(made, s => Assert.Equal("Bucket", s.Category));
		Assert.Equal(new[] { made[0] }.ToHashSet(), skeleton.ShownSkins);
		Assert.Equal(new[] { true, false, false }, layers.Select(l => skeleton.ResolveAttachment(l) != null));

		SkinOps.Show(skeleton, made[2]);   // wrecked
		Assert.Equal(new[] { false, false, true }, layers.Select(l => skeleton.ResolveAttachment(l) != null));
		Assert.NotNull(skeleton.ResolveAttachment(S(skeleton, "head")));
	}

	[Fact]
	public void A_parts_drawings_become_a_category_its_keys_showing_the_skins_drawing()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root" };
		skeleton.Roots.Add(root);
		var bucket = Part(skeleton, root, "bucket", "bucket_2", "bucket_3");
		var pixels = new[] { "bucket", "bucket_2", "bucket_3" }.Select(n => skeleton.DefaultSkin.Get("bucket", n)!.AssetId).ToList();
		var hit = new Animation { Name = "hit", Duration = 1f };
		var swaps = new AttachmentTimeline("bucket");
		swaps.Track.Add(0f, "bucket");
		swaps.Track.Add(0.5f, "bucket_3");
		hit.Timelines.Add(swaps);

		var made = SkinOps.MakeCategoryFromDrawings(skeleton, new[] { hit }, bucket, "Bucket");
		Assert.Equal(new[] { "bucket", "bucket_2", "bucket_3" }, made.Select(s => s.Name));
		Assert.Empty(skeleton.DefaultSkin.AttachmentsFor("bucket"));
		Assert.All(made, s => Assert.Equal("bucket", s.AttachmentsFor("bucket").Single().Name));   // one name for its drawing
		Assert.Equal(new[] { "bucket", "bucket" }, swaps.Track.Keys.Select(k => k.Value));
		Assert.Equal(new[] { made[0] }.ToHashSet(), skeleton.ShownSkins);   // the rest drawing's

		for (int i = 0; i < 3; i++)
		{
			SkinOps.Show(skeleton, made[i]);
			Assert.Equal(pixels[i], skeleton.ResolveAttachment(bucket)!.AssetId);
		}
		Assert.Equal(new[] { "bucket" }, DrawingOps.DrawingNames(skeleton, bucket));
	}

	[Fact]
	public void Deleting_a_skin_drops_its_drawings_and_its_empty_parts_and_dissolving_gives_them_back()
	{
		var skeleton = Zombie();
		var bucketPixels = PixelsOf(skeleton, "bucket");
		var bucket = SkinOps.MakeSkin(skeleton, "Bucket", new[] { S(skeleton, "bucket") });
		var bandage = SkinOps.MakeSkin(skeleton, "Bandage", new[] { S(skeleton, "bandage") }, overlay: true);
		var wave = new Animation { Name = "wave", Duration = 1f };
		wave.Timelines.Add(new AttachmentTimeline("bucket"));

		SkinOps.Delete(skeleton, new[] { wave }, bucket);
		Assert.Null(skeleton.FindSlot("bucket"));
		Assert.False(skeleton.Assets.ContainsKey(bucketPixels));
		Assert.Empty(wave.Timelines);

		SkinOps.Dissolve(skeleton, bandage);
		Assert.Empty(skeleton.Skins);
		Assert.True(SkinOps.HasOwnDrawings(skeleton, S(skeleton, "bandage")));
		Assert.NotNull(skeleton.ResolveAttachment(S(skeleton, "bandage")));
	}

	[Fact]
	public void Skins_are_saved_with_their_categories_overlays_and_which_are_on()
	{
		var skeleton = Zombie();
		var layers = new[] { "bucket", "bucket_dented" }.Select(n => S(skeleton, n)).ToList();
		var made = SkinOps.MakeCategoryFromParts(skeleton, "Bucket", layers);
		SkinOps.Show(skeleton, made[1]);
		SkinOps.MakeSkin(skeleton, "Bandage", new[] { S(skeleton, "bandage") }, overlay: true);
		var hidden = SkinOps.MakeSkin(skeleton, "Wrecked", new[] { S(skeleton, "bucket_wrecked") }, "Bucket");
		SkinOps.Show(skeleton, made[1]);

		var dto = ProjectMapper.ToDto(skeleton, Array.Empty<Animation>(), Fps);
		var (loaded, _) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(JsonSerializer.Serialize(dto, ProjectMapper.Json), ProjectMapper.Json)!);
		loaded.SetToSetupPose();
		Assert.Equal(new[] { "bucket", "bucket_dented", "Bandage", "Wrecked" }, loaded.Skins.Select(s => s.Name));
		Assert.Equal(new[] { "Bucket", "Bucket", "", "Bucket" }, loaded.Skins.Select(s => s.Category));
		Assert.Equal(new[] { false, false, true, false }, loaded.Skins.Select(s => s.Overlay));
		Assert.Equal(new[] { "bucket_dented", "Bandage" }.ToHashSet(), loaded.ShownSkins.Select(s => s.Name).ToHashSet());
		Assert.Equal(PixelsOf(skeleton, "bucket_dented"), PixelsOf(loaded, "bucket_dented"));
		Assert.Null(loaded.ResolveAttachment(loaded.FindSlot("bucket_wrecked")!));

		// A file from before: one skin on, by name
		dto.ShownSkins = null;
		dto.ActiveSkin = "Wrecked";
		var (old, _) = ProjectMapper.FromDto(dto);
		Assert.Equal(new[] { "Wrecked" }, old.ShownSkins.Select(s => s.Name));
		Assert.Equal(hidden.Name, old.ShownSkins.Single().Name);
	}
}
