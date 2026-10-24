using Animator.Core.Import;
using Animator.Core.Utilities;
using Animator.Viewer;
using Animator.Viewer.Import;
using Animator.Viewer.Rendering;
using SkiaSharp;

namespace Animator.Tests;

// Milestone 11.5: rigging finished art. Bones fitted over parts without moving them, bones deleted
// without losing what's on them, drawn objects given bones, parts put on the nearest bones.
public class RiggingTests
{
	private static void Near(Vector2 expected, Vector2 actual, float within = 1e-3f) =>
		Assert.True(Vector2.Distance(expected, actual) < within, $"expected {expected}, got {actual}");

	private static Slot Part(Skeleton skeleton, Bone bone, string name, Vector2 offset, float rotation = 0f)
	{
		var slot = new Slot(name, bone) { SetupAttachment = name, ActiveAttachment = name };
		skeleton.Slots.Add(slot);
		skeleton.DefaultSkin.Set(name, new Attachment { Name = name, Offset = offset, Rotation = rotation });
		return slot;
	}

	// Where a part's pivot is, and which way it faces, in the world
	private static (Vector2 At, float Angle) Placed(Skeleton skeleton, Slot slot)
	{
		var drawing = skeleton.DefaultSkin.Get(slot.Name, slot.SetupAttachment!)!;
		var frame = PartOps.FrameOf(drawing) * slot.Bone.World;
		return (frame.Translation, MathF.Atan2(frame.M12, frame.M11));
	}

	private static Dictionary<string, (Vector2 At, float Angle)> AllPlaced(Skeleton skeleton) =>
		skeleton.Slots.ToDictionary(s => s.Name, s => Placed(skeleton, s));

	private static void StayedPut(Skeleton skeleton, Dictionary<string, (Vector2 At, float Angle)> before)
	{
		foreach (var slot in skeleton.Slots)
		{
			var (at, angle) = Placed(skeleton, slot);
			Near(before[slot.Name].At, at);
			Assert.Equal(0, MathUtil.ShortestAngle(angle - before[slot.Name].Angle), 3);
		}
	}

	// A straight arm along x: the upper arm from (0, 0) to (100, 0), the forearm on its end to
	// (200, 0), the hand on that end to (250, 0), and a free badge bone on the upper arm at (50, 20).
	// A part on each.
	private static (Skeleton Skeleton, Bone Upper, Bone Fore, Bone Hand, Bone Badge) Arm()
	{
		var skeleton = new Skeleton();
		var upper = new Bone { Name = "upper", Length = 100 };
		var fore = new Bone { Name = "fore", Length = 100, Connected = true };
		var hand = new Bone { Name = "hand", Length = 50, Connected = true };
		var badge = new Bone { Name = "badge", Length = 10, SetupPosition = new Vector2(50, 20) };
		upper.AddChild(fore);
		fore.AddChild(hand);
		upper.AddChild(badge);
		skeleton.Roots.Add(upper);
		Part(skeleton, upper, "upper part", new Vector2(50, 0));
		Part(skeleton, fore, "fore part", new Vector2(50, 10), 0.3f);
		Part(skeleton, hand, "hand part", new Vector2(25, 0));
		Part(skeleton, badge, "badge part", new Vector2(5, 0));
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		return (skeleton, upper, fore, hand, badge);
	}

	// ======================= parts stay put =======================

	[Fact]
	public void Moving_a_joint_over_the_art_moves_nothing_else()
	{
		var (skeleton, upper, fore, hand, badge) = Arm();
		var before = AllPlaced(skeleton);

		var frozen = FrozenRest.Capture(skeleton);
		RestEdits.MoveJoint(frozen, fore, new Vector2(100, 30));   // the elbow

		Near(new Vector2(100, 30), fore.World.Translation);
		Near(new Vector2(100, 30), upper.TipWorld);    // the elbow is shared: the upper arm reaches it
		Assert.True(fore.Connected);
		Near(new Vector2(200, 0), fore.TipWorld);      // the wrist stays…
		Near(new Vector2(200, 0), hand.World.Translation);
		Near(new Vector2(250, 0), hand.TipWorld);      // …and the hand
		Near(new Vector2(50, 20), badge.World.Translation);
		StayedPut(skeleton, before);                   // and every drawing

		// The next mouse move starts from the same frozen rest: no drift
		RestEdits.MoveJoint(frozen, fore, new Vector2(110, -20));
		Near(new Vector2(110, -20), upper.TipWorld);
		Near(new Vector2(200, 0), fore.TipWorld);
		StayedPut(skeleton, before);
	}

	[Fact]
	public void Turning_or_stretching_a_bone_keeps_what_is_on_it_in_place()
	{
		var (skeleton, upper, fore, _, badge) = Arm();
		var before = AllPlaced(skeleton);

		RestEdits.Turn(FrozenRest.Capture(skeleton), upper, MathF.PI / 2);   // the upper arm points up now
		Near(new Vector2(0, 100), upper.TipWorld);
		Near(new Vector2(0, 100), fore.World.Translation);   // the elbow sits on its end, so it went along…
		Near(new Vector2(200, 0), fore.TipWorld);            // …but the wrist didn't
		Near(new Vector2(50, 20), badge.World.Translation);
		StayedPut(skeleton, before);

		RestEdits.Stretch(FrozenRest.Capture(skeleton), upper, 50);
		Near(new Vector2(0, 50), fore.World.Translation);
		Near(new Vector2(200, 0), fore.TipWorld);
		StayedPut(skeleton, before);
	}

	[Fact]
	public void A_free_joint_dragged_onto_its_parents_end_snaps_on()
	{
		var (skeleton, _, _, _, badge) = Arm();
		var before = AllPlaced(skeleton);

		RestEdits.MoveJoint(FrozenRest.Capture(skeleton), badge, new Vector2(102, 1), snapTo: 5);

		Assert.True(badge.Connected);
		Near(new Vector2(100, 0), badge.World.Translation);
		Near(new Vector2(60, 20), badge.TipWorld);   // its own end stayed
		StayedPut(skeleton, before);
	}

	// ======================= deleting bones =======================

	[Fact]
	public void Deleting_a_bone_can_keep_its_parts_and_bones_where_they_are()
	{
		var (skeleton, upper, _, hand, _) = Arm();
		var before = AllPlaced(skeleton);

		Assert.True(RigOps.DissolveBone(skeleton, new List<Animation>(), skeleton.FindBone("fore")!));
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();

		Assert.Null(skeleton.FindBone("fore"));
		Assert.Same(upper, hand.Parent);
		Assert.False(hand.Connected);   // it was on the forearm's end, not on the upper arm's
		Near(new Vector2(200, 0), hand.World.Translation);
		Near(new Vector2(250, 0), hand.TipWorld);
		Assert.Same(upper, skeleton.FindSlot("fore part")!.Bone);
		StayedPut(skeleton, before);

		Assert.False(RigOps.DissolveBone(skeleton, new List<Animation>(), upper));   // a root has nowhere to put them
	}

	// ======================= parts onto bones =======================

	// A part that's a solid bar, w × h pixels, centred at `middle`, on `bone`
	private static Slot Bar(Skeleton skeleton, AssetStore store, Bone bone, string name, int w, int h, Vector2 middle)
	{
		var asset = new Asset { Name = name, Width = w, Height = h };
		using (var pixels = new SKBitmap(AssetStore.InfoFor(w, h)))
		{
			pixels.Erase(SKColors.White);
			store.AddLayer(asset, "Base", pixels);
		}
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		var offset = Matrix3x2.Invert(bone.World, out var toBone) ? Vector2.Transform(middle, toBone) : middle;
		return PartOps.CreatePart(skeleton, bone, asset, name, offset, -bone.WorldRotation);
	}

	[Fact]
	public void A_part_goes_on_the_bone_it_lies_along_and_stays_where_it_is()
	{
		var skeleton = new Skeleton();
		var store = new AssetStore();
		var root = RigOps.AddBone(skeleton, null, "root", Vector2.Zero, -MathF.PI / 2, 100);   // down along the post
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		var arm = RigOps.AddBoneBetween(skeleton, root, new Vector2(0, 0), new Vector2(100, 0));
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		var leg = RigOps.AddBoneBetween(skeleton, root, new Vector2(0, -5), new Vector2(0, -100));
		var bar = Bar(skeleton, store, root, "bar", 80, 10, new Vector2(50, 0));
		var post = Bar(skeleton, store, root, "post", 10, 80, new Vector2(0, -50));
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		var before = AllPlaced(skeleton);

		Assert.Same(arm, PartRigging.NearestBone(skeleton, bar, store));
		Assert.Same(leg, PartRigging.NearestBone(skeleton, post, store));   // the root runs along it too, but it's the rig's handle
		Assert.True(PartOps.MoveToBone(skeleton, bar, arm));
		Assert.True(PartOps.MoveToBone(skeleton, post, leg));
		StayedPut(skeleton, before);
	}

	[Fact]
	public void A_drawn_object_gets_suggested_bones_with_every_part_staying_put()
	{
		// The figure of the Photoshop tests, brought in as a drawn object
		var colours = new[] { SKColors.LightBlue, SKColors.Red, SKColors.Bisque, SKColors.Orange, SKColors.Gold, SKColors.Black };
		var doc = new PsdDocument { Width = 200, Height = 300, Depth = 8 };
		foreach (var (name, (bounds, alpha), k) in PsdImportTests.Figure.Select((f, k) => (f.Name, f.Shape, k)))
		{
			var pixels = new byte[bounds.W * bounds.H * 4];
			for (int i = 0; i < alpha.Length; i++)
				if (alpha[i] > 0)
					(pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (colours[k].Red, colours[k].Green, colours[k].Blue, (byte)255);
			doc.Layers.Add(new PsdLayer { Name = name, Bounds = bounds, Pixels = pixels });
		}
		var store = new AssetStore();
		using var plan = PsdImportPlan.FromDocument(doc, "drawn");
		var obj = PsdImporter.IntoScene(new Scene(), store, plan, Vector2.Zero);
		var skeleton = obj.Skeleton;
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		var before = AllPlaced(skeleton);

		// What "Give it bones ▾ > Bones suggested from its parts" does
		obj.Kind = ObjectKind.Rigged;
		var root = skeleton.Roots[0];
		var frozen = FrozenRest.Capture(skeleton);
		root.Length = 40;
		RestEdits.Turn(frozen, root, MathF.PI / 2);
		int added = PartRigging.SuggestBones(skeleton, store, root);

		Assert.Equal(4, added);   // body, head, upper arm, forearm
		var bones = skeleton.AllBones().ToDictionary(b => b.Name);
		Assert.Same(root, bones["torso"].Parent);
		Assert.Same(bones["torso"], bones["head"].Parent);
		Assert.Same(bones["upper arm"], bones["forearm"].Parent);
		Assert.Same(bones["head"], skeleton.FindSlot("eye")!.Bone);
		Assert.Same(bones["forearm"], skeleton.FindSlot("forearm")!.Bone);
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
		StayedPut(skeleton, before);
	}
}
