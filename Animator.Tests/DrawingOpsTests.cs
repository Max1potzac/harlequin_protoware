namespace Animator.Tests;

// Frame-by-frame parts: drawings, keys and holds
public class DrawingOpsTests
{
	private const int Fps = 30;

	// A "mouth" part on a head bone, with two drawings: mouth_a (the rest drawing) and mouth_b
	private static (Skeleton Skeleton, Slot Slot, Animation Animation) Rig()
	{
		var skeleton = new Skeleton();
		var head = new Bone { Name = "head", Length = 50 };
		skeleton.Roots.Add(head);

		var slot = new Slot("mouth", head) { SetupAttachment = "mouth_a" };
		skeleton.Slots.Add(slot);
		foreach (var name in new[] { "mouth_a", "mouth_b" })
		{
			var asset = new Asset { Name = name, Width = 10, Height = 8 };
			skeleton.Assets[asset.Id] = asset;
			skeleton.DefaultSkin.Set("mouth", new Attachment
			{
				Name = name, AssetId = asset.Id, Offset = new Vector2(5, 2), Rotation = 0.5f, Pivot = new Vector2(0.25f, 1f)
			});
		}

		var animation = new Animation { Name = "talk", Duration = 24f / Fps };   // 24 frames
		return (skeleton, slot, animation);
	}

	private static float T(int frame) => frame / (float)Fps;

	// The drawing row's keys as (frame, drawing) pairs
	private static List<(int, string?)> Row(Animation animation, Slot slot) =>
		animation.Find<AttachmentTimeline>(slot.Name)!.Track.Keys
				 .Select(k => ((int)MathF.Round(k.Time * Fps), k.Value))
				 .ToList();

	private static void Keys(Animation animation, Slot slot, params (int Frame, string Drawing)[] keys)
	{
		var row = animation.GetOrCreate(slot.Name, n => new AttachmentTimeline(n));
		foreach (var (frame, drawing) in keys) row.Add(T(frame), drawing);
	}

	[Fact]
	public void Names_compare_numbers_as_numbers()
	{
		var (skeleton, slot, _) = Rig();
		foreach (var name in new[] { "mouth_10", "mouth_2" })
			skeleton.DefaultSkin.Set("mouth", new Attachment { Name = name });

		var names = DrawingOps.DrawingNames(skeleton, slot);

		Assert.True(names.IndexOf("mouth_2") < names.IndexOf("mouth_10"));
		Assert.Equal(4, names.Count);
	}

	[Fact]
	public void A_new_drawing_is_placed_like_its_template_and_gets_a_free_name()
	{
		var (skeleton, slot, _) = Rig();
		var template = skeleton.DefaultSkin.Get("mouth", "mouth_b")!;

		var first = DrawingOps.AddDrawing(skeleton, slot, template, new Asset { Width = 10, Height = 8 });
		var second = DrawingOps.AddDrawing(skeleton, slot, template, new Asset { Width = 10, Height = 8 });

		Assert.Equal("mouth_2", first.Name);
		Assert.Equal("mouth_3", second.Name);
		Assert.Equal(template.Offset, first.Offset);
		Assert.Equal(template.Rotation, first.Rotation);
		Assert.Equal(template.Pivot, first.Pivot);
		Assert.Equal("mouth_2", skeleton.Assets[first.AssetId].Name);
		Assert.Same(first, skeleton.DefaultSkin.Get("mouth", "mouth_2"));
	}

	[Fact]
	public void Showing_a_drawing_keys_it_from_that_frame_on()
	{
		var (_, slot, animation) = Rig();

		Assert.True(DrawingOps.Show(animation, slot, "mouth_b", 6, Fps, key: true));

		Assert.Equal("mouth_a", DrawingOps.DrawingAt(animation, slot, T(5)));   // earlier frames: the rest drawing
		Assert.Equal("mouth_b", DrawingOps.DrawingAt(animation, slot, T(6)));
		Assert.Equal("mouth_b", DrawingOps.DrawingAt(animation, slot, T(20)));
		Assert.False(DrawingOps.Show(animation, slot, "mouth_b", 10, Fps, key: true));   // already showing
	}

	[Fact]
	public void Showing_a_drawing_in_setup_mode_changes_the_rest_drawing()
	{
		var (_, slot, animation) = Rig();

		Assert.True(DrawingOps.Show(animation, slot, "mouth_b", 6, Fps, key: false));

		Assert.Equal("mouth_b", slot.SetupAttachment);
		Assert.Null(animation.Find<AttachmentTimeline>(slot.Name));
	}

	[Fact]
	public void Hiding_is_a_drawing_key_too()
	{
		var (_, slot, animation) = Rig();

		Assert.True(DrawingOps.Show(animation, slot, null, 3, Fps, key: true));

		Assert.Null(DrawingOps.DrawingAt(animation, slot, T(3)));
		Assert.Equal("mouth_a", DrawingOps.DrawingAt(animation, slot, T(2)));
	}

	[Fact]
	public void Longer_moves_the_later_drawings()
	{
		var (_, slot, animation) = Rig();
		Keys(animation, slot, (0, "mouth_a"), (4, "mouth_b"), (8, "mouth_a"));

		Assert.Equal(HoldChange.Done, DrawingOps.InsertFrame(animation, slot, 5, Fps, 24));
		Assert.Equal(new List<(int, string?)> { (0, "mouth_a"), (4, "mouth_b"), (9, "mouth_a") }, Row(animation, slot));

		Assert.Equal(HoldChange.Done, DrawingOps.InsertFrame(animation, slot, 2, Fps, 24));
		Assert.Equal(new List<(int, string?)> { (0, "mouth_a"), (5, "mouth_b"), (10, "mouth_a") }, Row(animation, slot));
	}

	[Fact]
	public void Longer_is_refused_when_the_last_drawing_would_fall_off_the_end()
	{
		var (_, slot, animation) = Rig();
		Keys(animation, slot, (0, "mouth_a"), (23, "mouth_b"));

		Assert.Equal(HoldChange.NoRoom, DrawingOps.InsertFrame(animation, slot, 10, Fps, 24));
		Assert.Equal(new List<(int, string?)> { (0, "mouth_a"), (23, "mouth_b") }, Row(animation, slot));
	}

	[Fact]
	public void The_last_drawing_already_lasts_until_the_end()
	{
		var (_, slot, animation) = Rig();
		Keys(animation, slot, (0, "mouth_a"), (4, "mouth_b"));

		Assert.Equal(HoldChange.NothingAfter, DrawingOps.InsertFrame(animation, slot, 6, Fps, 24));
		Assert.Equal(HoldChange.NothingAfter, DrawingOps.RemoveFrame(animation, slot, 6, Fps));
	}

	[Fact]
	public void Shorter_moves_the_later_drawings_and_drops_a_one_frame_drawing()
	{
		var (_, slot, animation) = Rig();
		Keys(animation, slot, (0, "mouth_a"), (4, "mouth_b"), (5, "mouth_a"), (8, "mouth_b"));

		// mouth_b at frame 4 is on screen for one frame only: it goes, and the rest moves up
		Assert.Equal(HoldChange.Done, DrawingOps.RemoveFrame(animation, slot, 4, Fps));
		Assert.Equal(new List<(int, string?)> { (0, "mouth_a"), (4, "mouth_a"), (7, "mouth_b") }, Row(animation, slot));

		Assert.Equal(HoldChange.Done, DrawingOps.RemoveFrame(animation, slot, 1, Fps));
		Assert.Equal(new List<(int, string?)> { (0, "mouth_a"), (3, "mouth_a"), (6, "mouth_b") }, Row(animation, slot));
	}

	[Fact]
	public void Deleting_a_drawing_removes_its_keys_and_pixels_but_keeps_one_drawing()
	{
		var (skeleton, slot, animation) = Rig();
		var pixels = skeleton.DefaultSkin.Get("mouth", "mouth_b")!.AssetId;
		Keys(animation, slot, (0, "mouth_a"), (6, "mouth_b"), (12, "mouth_a"));

		Assert.True(DrawingOps.DeleteDrawing(skeleton, new[] { animation }, slot, "mouth_b"));

		Assert.Equal(new List<string> { "mouth_a" }, DrawingOps.DrawingNames(skeleton, slot));
		Assert.Equal(new List<(int, string?)> { (0, "mouth_a"), (12, "mouth_a") }, Row(animation, slot));
		Assert.False(skeleton.Assets.ContainsKey(pixels));
		Assert.False(DrawingOps.DeleteDrawing(skeleton, new[] { animation }, slot, "mouth_a"));   // the last one stays
	}

	[Fact]
	public void Deleting_the_rest_drawing_picks_another_one()
	{
		var (skeleton, slot, animation) = Rig();

		Assert.True(DrawingOps.DeleteDrawing(skeleton, new[] { animation }, slot, "mouth_a"));

		Assert.Equal("mouth_b", slot.SetupAttachment);
	}

	[Fact]
	public void Renaming_a_drawing_updates_its_keys_rest_drawing_and_pixels()
	{
		var (skeleton, slot, animation) = Rig();
		Keys(animation, slot, (0, "mouth_b"), (6, "mouth_a"));

		Assert.True(DrawingOps.RenameDrawing(skeleton, new[] { animation }, slot, "mouth_a", "closed"));

		Assert.Equal("closed", slot.SetupAttachment);
		Assert.Equal(new List<(int, string?)> { (0, "mouth_b"), (6, "closed") }, Row(animation, slot));
		var drawing = skeleton.DefaultSkin.Get("mouth", "closed")!;
		Assert.Equal("closed", drawing.Name);
		Assert.Equal("closed", skeleton.Assets[drawing.AssetId].Name);
		Assert.Null(skeleton.DefaultSkin.Get("mouth", "mouth_a"));

		Assert.False(DrawingOps.RenameDrawing(skeleton, new[] { animation }, slot, "mouth_b", "closed"));   // taken
		Assert.False(DrawingOps.RenameDrawing(skeleton, new[] { animation }, slot, "mouth_b", "  "));       // empty
	}
}
