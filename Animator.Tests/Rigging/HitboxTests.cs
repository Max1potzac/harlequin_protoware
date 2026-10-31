using System.Text.Json;
using Animator.Core.Export;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Step 20: hitboxes and points on bones, keyed on and off, saved, and exported per frame
public class HitboxTests
{
	private const int Fps = 30;

	private static void Near(Vector2 expected, Vector2 actual, float within = 1e-3f) =>
		Assert.True(Vector2.Distance(expected, actual) < within, $"expected {expected}, got {actual}");

	// A root at the origin, an arm 100 along it pointing up (turned a quarter), 50 long
	private static (Skeleton Skeleton, Bone Root, Bone Arm) Rig()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 40 };
		var arm = new Bone { Name = "arm", Length = 50, SetupPosition = new Vector2(100, 0), SetupRotation = MathF.PI / 2 };
		root.AddChild(arm);
		skeleton.Roots.Add(root);
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		return (skeleton, root, arm);
	}

	[Fact]
	public void A_hitbox_moves_and_turns_with_its_bone()
	{
		var (skeleton, _, arm) = Rig();
		var box = HitboxOps.AddHitbox(skeleton, arm, HitboxShape.Box, "hit");
		box.Offset = new Vector2(20, 0);
		box.Size = new Vector2(40, 10);   // 40 along the arm, 10 across

		Near(new Vector2(100, 20), box.World.Translation);
		Assert.True(box.Contains(new Vector2(103, 38)));    // up the arm, a little to the side
		Assert.False(box.Contains(new Vector2(110, 20)));   // too far across
		var corners = box.Outline();
		Assert.Equal(4, corners.Length);
		Assert.Equal(40f, Vector2.Distance(corners[0], corners[1]), 3);

		var ring = HitboxOps.AddHitbox(skeleton, arm, HitboxShape.Circle);
		ring.Offset = Vector2.Zero;
		ring.Size = new Vector2(20);
		Assert.True(ring.Contains(new Vector2(109, 0)));
		Assert.False(ring.Contains(new Vector2(111, 0)));

		var point = HitboxOps.AddPoint(skeleton, arm);   // at its tip, facing along it
		Near(new Vector2(100, 50), point.WorldPosition);
		Assert.Equal(MathF.PI / 2, point.WorldAngle, 4);
	}

	[Fact]
	public void Hitboxes_are_keyed_on_and_off_and_come_back_to_rest()
	{
		var (skeleton, _, arm) = Rig();
		var swipe = HitboxOps.AddHitbox(skeleton, arm, HitboxShape.Box, "hit");
		swipe.SetupActive = swipe.Active = false;
		var attack = new Animation { Name = "attack", Duration = 1f };

		Assert.False(HitboxOps.KeyActive(attack, swipe, 0, Fps, false));   // off already
		Assert.True(HitboxOps.KeyActive(attack, swipe, 10, Fps, true));
		Assert.True(HitboxOps.KeyActive(attack, swipe, 14, Fps, false));
		Assert.False(HitboxOps.KeyActive(attack, swipe, 14, Fps, false));

		bool OnAt(int frame)
		{
			skeleton.SetToSetupPose();
			attack.Apply(skeleton, frame / (float)Fps);
			return swipe.Active;
		}
		Assert.Equal(new[] { false, true, true, false, false }, new[] { 5, 10, 13, 14, 25 }.Select(OnAt));
		Assert.Equal("arm hit (hitbox on)", attack.Find<HitboxTimeline>(swipe.Name)!.Label);

		Assert.True(HitboxOps.RenameHitbox(skeleton, new[] { attack }, swipe, "claw"));
		Assert.NotNull(attack.Find<HitboxTimeline>("claw"));
		Assert.True(OnAt(12));
	}

	[Fact]
	public void A_hitbox_fits_round_a_parts_pixels_turning_with_it()
	{
		var (skeleton, root, arm) = Rig();
		var slot = new Slot("sword", arm) { SetupAttachment = "sword", ActiveAttachment = "sword" };
		skeleton.Slots.Add(slot);
		var asset = new Asset { Name = "sword", Width = 60, Height = 20 };
		skeleton.Assets[asset.Id] = asset;
		skeleton.DefaultSkin.Set("sword", new Attachment { Name = "sword", AssetId = asset.Id, Offset = new Vector2(30, 0) });

		var box = HitboxOps.AddHitbox(skeleton, arm, HitboxShape.Box);
		Assert.True(HitboxOps.FitToPart(skeleton, box, slot));
		Near(new Vector2(30, 0), box.Offset);
		Assert.Equal(60f, box.Size.X, 3);
		Assert.Equal(20f, box.Size.Y, 3);
		Assert.Equal(0f, box.Rotation, 4);

		// On another bone (the root, turned a quarter from the arm): it turns to match the picture
		var onRoot = HitboxOps.AddHitbox(skeleton, root, HitboxShape.Box);
		Assert.True(HitboxOps.FitToPart(skeleton, onRoot, slot, (0f, 0f, 30f, 20f)));   // just the left half of the picture
		Near(new Vector2(100, 15), onRoot.World.Translation);
		Assert.Equal(30f, onRoot.Size.X, 3);
		Assert.Equal(MathF.PI / 2, MathF.Abs(onRoot.Rotation), 4);
	}

	[Fact]
	public void Deleting_a_bone_takes_its_hitboxes_and_keys_and_dissolving_it_keeps_them_in_place()
	{
		var (skeleton, root, arm) = Rig();
		var hand = new Bone { Name = "hand", Length = 10, SetupPosition = new Vector2(50, 0) };
		arm.AddChild(hand);
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		var grab = HitboxOps.AddHitbox(skeleton, hand, HitboxShape.Circle, "hit");
		var hold = HitboxOps.AddPoint(skeleton, hand);
		var swing = new Animation { Name = "swing", Duration = 1f };
		HitboxOps.KeyActive(swing, grab, 5, Fps, false);

		var armBox = HitboxOps.AddHitbox(skeleton, arm, HitboxShape.Box);
		var before = armBox.World.Translation;
		Assert.True(RigOps.DissolveBone(skeleton, new[] { swing }, arm));
		skeleton.UpdateWorld();
		Assert.Same(root, armBox.Bone);
		Near(before, armBox.World.Translation);

		var holdAt = hold.WorldPosition;
		RigOps.DeleteBone(skeleton, new[] { swing }, hand);
		Assert.DoesNotContain(grab, skeleton.Hitboxes);
		Assert.DoesNotContain(hold, skeleton.Points);
		Assert.Empty(swing.Timelines);
		Assert.NotEqual(Vector2.Zero, holdAt);
	}

	[Fact]
	public void Hitboxes_points_and_their_keys_are_saved_and_undone()
	{
		var (skeleton, _, arm) = Rig();
		var block = HitboxOps.AddHitbox(skeleton, arm, HitboxShape.Box, "block");
		block.Offset = new Vector2(5, 6);
		block.Rotation = 0.25f;
		block.Size = new Vector2(30, 12);
		block.SetupActive = false;
		var head = HitboxOps.AddHitbox(skeleton, arm, HitboxShape.Circle, "weak spot");
		var muzzle = HitboxOps.AddPoint(skeleton, arm, "muzzle");
		muzzle.Rotation = -0.5f;
		var guard = new Animation { Name = "guard", Duration = 1f };
		HitboxOps.KeyActive(guard, block, 3, Fps, true);

		var dto = ProjectMapper.ToDto(skeleton, new[] { guard }, Fps);
		var (loaded, animations) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(JsonSerializer.Serialize(dto, ProjectMapper.Json), ProjectMapper.Json)!);
		var b = loaded.FindHitbox(block.Name)!;
		Assert.Equal((HitboxShape.Box, "block", false, "arm"), (b.Shape, b.Kind, b.SetupActive, b.Bone.Name));
		Near(new Vector2(5, 6), b.Offset);
		Near(new Vector2(30, 12), b.Size);
		Assert.Equal(0.25f, b.Rotation, 5);
		Assert.Equal((HitboxShape.Circle, "weak spot"), (loaded.FindHitbox(head.Name)!.Shape, loaded.FindHitbox(head.Name)!.Kind));
		Assert.Equal(-0.5f, loaded.FindPoint("muzzle")!.Rotation, 5);
		var keys = animations.Single().Find<HitboxTimeline>(block.Name)!.Track.Keys;
		Assert.Equal(new[] { true }, keys.Select(k => k.Value));

		// Undo: as it was, the same objects
		var snapshot = StructureSnapshot.Capture(skeleton, new List<Animation> { guard });
		block.Size = new Vector2(1, 1);
		HitboxOps.DeleteHitbox(skeleton, new[] { guard }, head);
		skeleton.Points.Clear();
		snapshot.Restore();
		Assert.Equal(new[] { block, head }, skeleton.Hitboxes);
		Assert.Equal(new[] { muzzle }, skeleton.Points);
		Near(new Vector2(30, 12), block.Size);
	}

	[Fact]
	public void A_sprite_sheet_frame_has_the_hitboxes_that_are_on_and_the_points_in_its_pixels()
	{
		var (skeleton, _, arm) = Rig();
		var box = HitboxOps.AddHitbox(skeleton, arm, HitboxShape.Box, "hurt", "body");
		box.Offset = new Vector2(20, 0);
		box.Size = new Vector2(40, 10);
		var off = HitboxOps.AddHitbox(skeleton, arm, HitboxShape.Circle, "hit", "claw");
		off.Active = false;
		HitboxOps.AddPoint(skeleton, arm, "tip");

		// A frame whose top-left is the object's (0, 100), at twice the size (y down)
		var toPixels = Matrix3x2.CreateTranslation(0, -100) * Matrix3x2.CreateScale(2, -2);
		var hitboxes = SheetMarkers.Hitboxes(skeleton, toPixels, "zombie/");
		var only = Assert.Single(hitboxes);
		Assert.Equal(("zombie/body", "hurt", "box"), (only.Name, only.Kind, only.Shape));
		Assert.Equal((200f, 160f), (only.X, only.Y));        // (100, 20) in the object
		Assert.Equal((80f, 20f), (only.W!.Value, only.H!.Value));
		Assert.Equal(90f, MathF.Abs(only.Angle!.Value), 2);   // along the arm: straight up the picture
		var point = Assert.Single(SheetMarkers.Points(skeleton, toPixels));
		Assert.Equal(("tip", 200f, 100f), (point.Name, point.X, point.Y));

		var json = new SpriteSheetJson();
		json.Frames.Add(new SheetFrame { Filename = "a 0", Hitboxes = hitboxes });
		Assert.Contains("\"hitboxes\"", json.ToJson());
		Assert.DoesNotContain("\"points\"", json.ToJson());
	}
}
