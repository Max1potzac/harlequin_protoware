using System.IO;
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

	// Every part is where it was, within `within` (a pixel's thousandth; after many edits, its hundredth)
	private static void StayedPut(Skeleton skeleton, Dictionary<string, (Vector2 At, float Angle)> before, float within = 1e-3f)
	{
		foreach (var slot in skeleton.Slots)
		{
			var (at, angle) = Placed(skeleton, slot);
			Assert.True(Vector2.Distance(before[slot.Name].At, at) < within,
				$"{slot.Name} moved from {before[slot.Name].At} to {at}");
			Assert.Equal(0, MathUtil.ShortestAngle(angle - before[slot.Name].Angle), 3);
		}
	}

	private static void Rest(Skeleton skeleton)
	{
		skeleton.SetToSetupPose();
		skeleton.UpdateWorld();
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
		var (skeleton, upper, _, _, badge) = Arm();
		var before = AllPlaced(skeleton);

		var frozen = FrozenRest.Capture(skeleton);   // one drag: every mouse move starts from here
		RestEdits.MoveJoint(frozen, badge, new Vector2(102, 1), snapTo: 5);

		Assert.True(badge.Connected);
		Near(new Vector2(100, 0), badge.World.Translation);
		Near(new Vector2(60, 20), badge.TipWorld);   // its own end stayed
		StayedPut(skeleton, before);

		// Pulled on past it, it lets go again, and the upper arm's end isn't dragged along
		RestEdits.MoveJoint(frozen, badge, new Vector2(80, 10), snapTo: 5);
		Assert.False(badge.Connected);
		Near(new Vector2(80, 10), badge.World.Translation);
		Near(new Vector2(60, 20), badge.TipWorld);
		Near(new Vector2(100, 0), upper.TipWorld);
		StayedPut(skeleton, before);
	}

	[Fact]
	public void An_elbow_moves_as_one_joint_until_it_is_freed()
	{
		// Dragged twice: the second drag starts glued too, so it moves the shared joint again
		var (skeleton, upper, fore, _, _) = Arm();
		var before = AllPlaced(skeleton);
		RestEdits.MoveJoint(FrozenRest.Capture(skeleton), fore, new Vector2(90, 20));
		RestEdits.MoveJoint(FrozenRest.Capture(skeleton), fore, new Vector2(95, -10), snapTo: 5);
		Assert.True(fore.Connected);
		Near(new Vector2(95, -10), upper.TipWorld);
		Near(new Vector2(200, 0), fore.TipWorld);
		StayedPut(skeleton, before);

		// Freed first (the way typing a place in the bone panel does), it moves on its own
		RigOps.SetConnected(fore, false);
		RestEdits.MoveJoint(FrozenRest.Capture(skeleton), fore, new Vector2(120, 0));
		Assert.False(fore.Connected);
		Near(new Vector2(95, -10), upper.TipWorld);
		Near(new Vector2(120, 0), fore.World.Translation);
		Near(new Vector2(200, 0), fore.TipWorld);
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

		// Give it bones ▾ > Bones suggested from its parts
		var root = skeleton.Roots[0];
		int added = PartRigging.GiveBones(obj, store, 40, suggest: true);

		Assert.Equal(ObjectKind.Rigged, obj.Kind);
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

	// ======================= a whole character, start to finish =======================

	// A round-ended stroke from A to B, R on each side of it (a dot when A = B), in picture pixels
	private static (Vector2 A, Vector2 B, float R) Stroke(float x0, float y0, float x1, float y1, float r) =>
		(new Vector2(x0, y0), new Vector2(x1, y1), r);

	private static float ToSegment(Vector2 p, Vector2 a, Vector2 b)
	{
		var ab = b - a;
		float t = ab.LengthSquared() < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
		return Vector2.Distance(p, a + ab * t);
	}

	// A Photoshop layer of one colour, the shape of a stroke
	private static PsdLayer Layer(string name, SKColor colour, (Vector2 A, Vector2 B, float R) s)
	{
		int x0 = (int)MathF.Floor(MathF.Min(s.A.X, s.B.X) - s.R), y0 = (int)MathF.Floor(MathF.Min(s.A.Y, s.B.Y) - s.R);
		int x1 = (int)MathF.Ceiling(MathF.Max(s.A.X, s.B.X) + s.R), y1 = (int)MathF.Ceiling(MathF.Max(s.A.Y, s.B.Y) + s.R);
		var bounds = new PixelRect(x0, y0, x1 - x0, y1 - y0);
		var pixels = new byte[bounds.W * bounds.H * 4];
		for (int y = 0; y < bounds.H; y++)
			for (int x = 0; x < bounds.W; x++)
			{
				if (ToSegment(new Vector2(x0 + x + 0.5f, y0 + y + 0.5f), s.A, s.B) > s.R) continue;
				int i = (y * bounds.W + x) * 4;
				(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (colour.Red, colour.Green, colour.Blue, (byte)255);
			}
		return new PsdLayer { Name = name, Bounds = bounds, Pixels = pixels };
	}

	// A character standing with its arms held out, on a 400 × 520 picture, in 17 layers the way a
	// Photoshop file has them: bottom to top, the far arm and leg, the body, the near leg and arm
	private static readonly (string Name, (Vector2 A, Vector2 B, float R) Shape)[] Character =
	{
		("back upper arm", Stroke(232, 148, 286, 212, 12)),
		("back forearm", Stroke(286, 212, 326, 280, 10)),
		("back hand", Stroke(332, 292, 332, 292, 14)),
		("back thigh", Stroke(220, 288, 226, 378, 15)),
		("back shin", Stroke(226, 378, 229, 458, 12)),
		("back foot", Stroke(229, 468, 262, 472, 10)),
		("hips", Stroke(180, 272, 220, 272, 26)),
		("torso", Stroke(200, 160, 200, 240, 38)),
		("neck", Stroke(200, 104, 200, 140, 11)),
		("head", Stroke(200, 64, 200, 64, 44)),
		("eye", Stroke(216, 56, 216, 56, 6)),
		("front thigh", Stroke(180, 288, 174, 378, 15)),
		("front shin", Stroke(174, 378, 171, 458, 12)),
		("front foot", Stroke(171, 468, 138, 472, 10)),
		("front upper arm", Stroke(168, 148, 114, 212, 12)),
		("front forearm", Stroke(114, 212, 74, 280, 10)),
		("front hand", Stroke(68, 292, 68, 292, 14))
	};

	// The character brought in from its layers as a drawn object, the way File > Import does it
	private static (SceneObject Obj, AssetStore Store) DrawnCharacter()
	{
		var doc = new PsdDocument { Width = 400, Height = 520, Depth = 8 };
		for (int k = 0; k < Character.Length; k++)
			doc.Layers.Add(Layer(Character[k].Name, new SKColor((byte)(40 + k * 12), 140, (byte)(230 - k * 11)), Character[k].Shape));
		var store = new AssetStore();
		using var plan = PsdImportPlan.FromDocument(doc, "character");
		var obj = PsdImporter.IntoScene(new Scene(), store, plan, Vector2.Zero);
		Assert.Equal(ObjectKind.Drawn, obj.Kind);
		Rest(obj.Skeleton);
		return (obj, store);
	}

	// A picture pixel in the object: the picture's middle is the object's origin, and y points up
	private static Vector2 O(float x, float y) => new(x - 200, 260 - y);

	// What Ctrl+drag does: a bone under `parent` from one picture pixel to another…
	private static Bone Draw(Skeleton skeleton, Bone parent, float x0, float y0, float x1, float y1)
	{
		Rest(skeleton);
		var bone = RigOps.AddBoneBetween(skeleton, parent, O(x0, y0), O(x1, y1));
		Rest(skeleton);
		return bone;
	}

	// …or started on the parent's end, so it's glued there
	private static Bone DrawOn(Skeleton skeleton, Bone parent, float x1, float y1)
	{
		Rest(skeleton);
		var bone = RigOps.AddBoneBetween(skeleton, parent, parent.TipWorld, O(x1, y1));
		bone.Connected = true;
		Rest(skeleton);
		return bone;
	}

	// Posing `turned` (Animate mode) moves the parts on it and below it, and nothing else
	private static void TurningMovesWhatsOnIt(Skeleton skeleton, Bone turned, Dictionary<string, (Vector2 At, float Angle)> rest)
	{
		Rest(skeleton);
		turned.Rotation = turned.SetupRotation + 0.5f;
		skeleton.UpdateWorld();
		bool anyMoved = false;
		foreach (var slot in skeleton.Slots)
		{
			bool carried = slot.Bone == turned || slot.Bone.IsDescendantOf(turned);
			float moved = Vector2.Distance(rest[slot.Name].At, Placed(skeleton, slot).At);
			if (carried) anyMoved |= moved > 1f;
			else Assert.True(moved < 0.01f, $"{slot.Name} moved {moved} though it isn't on {turned.Name}");
		}
		Assert.True(anyMoved, $"nothing on {turned.Name} moved");
		Rest(skeleton);
	}

	[Fact]
	public void A_drawn_character_gets_bones_drawn_by_hand_and_works_as_a_rig()
	{
		var (obj, store) = DrawnCharacter();
		var skeleton = obj.Skeleton;
		var before = AllPlaced(skeleton);

		// Give it bones ▾ > Just a root bone: it's rigged now, the root pointing up from its origin
		Assert.Equal(0, PartRigging.GiveBones(obj, store, 40, suggest: false));
		Assert.Equal(ObjectKind.Rigged, obj.Kind);
		var root = Assert.Single(skeleton.Roots);
		Rest(skeleton);
		Near(new Vector2(0, 40), root.TipWorld);
		StayedPut(skeleton, before);

		// Ctrl+drag the bones over the picture: up the body to the head, out along the arms, down the legs
		var torso = Draw(skeleton, root, 200, 272, 200, 140);
		var neck = DrawOn(skeleton, torso, 200, 104);
		var head = DrawOn(skeleton, neck, 200, 20);
		var expected = new Dictionary<string, Bone> { ["torso"] = torso, ["neck"] = neck, ["head"] = head, ["eye"] = head };
		foreach (var (side, s) in new[] { ("front", -1f), ("back", 1f) })   // the front arm and leg are on the left
		{
			var upper = Draw(skeleton, torso, 200 + 32 * s, 148, 200 + 86 * s, 212);
			var fore = DrawOn(skeleton, upper, 200 + 126 * s, 280);
			var hand = DrawOn(skeleton, fore, 200 + 138 * s, 304);
			var thigh = Draw(skeleton, root, 200 + 20 * s, 288, 200 + 26 * s, 378);
			var shin = DrawOn(skeleton, thigh, 200 + 29 * s, 458);
			var foot = DrawOn(skeleton, shin, 200 + 62 * s, 472);
			expected[$"{side} upper arm"] = upper;
			expected[$"{side} forearm"] = fore;
			expected[$"{side} hand"] = hand;
			expected[$"{side} thigh"] = thigh;
			expected[$"{side} shin"] = shin;
			expected[$"{side} foot"] = foot;
		}
		Assert.Equal(16, skeleton.AllBones().Count());
		StayedPut(skeleton, before);

		// Bind ▾ > Every part onto its nearest bone
		Assert.True(PartRigging.PartsToNearestBones(skeleton, store, skeleton.Slots) > 0);
		Rest(skeleton);
		StayedPut(skeleton, before);
		foreach (var (part, bone) in expected)
			Assert.True(skeleton.FindSlot(part)!.Bone == bone, $"{part} went on {skeleton.FindSlot(part)!.Bone.Name}");
		Assert.DoesNotContain(skeleton.Slots, slot => slot.Bone == root);

		// The front elbow wasn't quite on the picture's elbow: drag it there
		var frozen = FrozenRest.Capture(skeleton);
		RestEdits.MoveJoint(frozen, expected["front forearm"], O(116, 206));
		Rest(skeleton);
		Near(O(116, 206), expected["front upper arm"].TipWorld);
		Near(O(116, 206), expected["front forearm"].World.Translation);
		Near(O(62, 304), expected["front hand"].TipWorld);
		StayedPut(skeleton, before, 0.01f);

		// It works as a rig: turning the front upper arm swings the front arm, and nothing else
		var arm = expected["front upper arm"];
		Assert.Equal(new[] { "front forearm", "front hand", "front upper arm" },
			skeleton.Slots.Where(slot => slot.Bone == arm || slot.Bone.IsDescendantOf(arm)).Select(slot => slot.Name).OrderBy(name => name));
		TurningMovesWhatsOnIt(skeleton, arm, before);
		TurningMovesWhatsOnIt(skeleton, torso, before);
	}

	[Fact]
	public void A_drawn_character_given_suggested_bones_can_be_fixed_by_hand()
	{
		var (obj, store) = DrawnCharacter();
		var skeleton = obj.Skeleton;
		var before = AllPlaced(skeleton);

		// Give it bones ▾ > Bones suggested from its parts
		Assert.Equal(16, PartRigging.GiveBones(obj, store, 40, suggest: true));   // all but the eye, which rides on the head
		Rest(skeleton);
		StayedPut(skeleton, before, 0.01f);
		var root = Assert.Single(skeleton.Roots);
		var bones = skeleton.AllBones().ToDictionary(b => b.Name);
		Assert.Same(root, bones["torso"].Parent);   // the middle of the figure
		Assert.Same(bones["torso"], bones["hips"].Parent);
		Assert.Same(bones["neck"], bones["head"].Parent);
		Assert.Same(bones["front upper arm"], bones["front forearm"].Parent);
		Assert.Same(bones["front forearm"], bones["front hand"].Parent);
		Assert.Same(bones["back thigh"], bones["back shin"].Parent);
		Assert.Same(bones["back shin"], bones["back foot"].Parent);
		Assert.Same(bones["head"], skeleton.FindSlot("eye")!.Bone);
		foreach (var slot in skeleton.Slots.Where(slot => slot.Name != "eye"))
			Assert.Same(bones[slot.Name], slot.Bone);   // each part on its own bone

		FixUpByHand(skeleton, store, before);
	}

	// Your own files: put Photoshop files in Animator.Tests/PrivateFiles (git leaves them out) and
	// run the tests. Each comes in as a drawn object, gets suggested bones and is fixed up by hand,
	// every part staying where it was. (Nothing there: nothing to check.)
	[Fact]
	public void Photoshop_files_of_your_own_can_be_given_bones_and_fixed_by_hand()
	{
		string folder = Path.Combine(AppContext.BaseDirectory, "PrivateFiles");
		if (!Directory.Exists(folder)) return;
		foreach (string path in Directory.GetFiles(folder).Where(f => f.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) ||
																	   f.EndsWith(".psb", StringComparison.OrdinalIgnoreCase)))
		{
			var store = new AssetStore();
			using var plan = PsdImportPlan.FromDocument(PsdReader.Read(path), Path.GetFileNameWithoutExtension(path));
			var obj = PsdImporter.IntoScene(new Scene(), store, plan, Vector2.Zero);
			var skeleton = obj.Skeleton;
			Rest(skeleton);
			var before = AllPlaced(skeleton);

			Assert.True(PartRigging.GiveBones(obj, store, 40, suggest: true) > 0, $"{Path.GetFileName(path)}: no bones suggested");
			Rest(skeleton);
			StayedPut(skeleton, before, 0.01f);
			FixUpByHand(skeleton, store, before);
		}
	}

	// Fixing up a rig by hand, each step checked against where every part was (`before`): every
	// joint dragged (one on its parent's end takes the end along), every bone turned and stretched,
	// a bone dragged onto another in the list, one deleted keeping what's on it, then every part
	// onto its nearest bone. After all that, posing a bone moves what's on it, and nothing else.
	private static void FixUpByHand(Skeleton skeleton, AssetStore store, Dictionary<string, (Vector2 At, float Angle)> before)
	{
		var root = Assert.Single(skeleton.Roots);
		var bones = skeleton.AllBones().Where(b => b.Parent != null).ToList();
		Assert.NotEmpty(bones);

		foreach (var bone in bones)
		{
			var frozen = FrozenRest.Capture(skeleton);
			RestEdits.MoveJoint(frozen, bone, frozen.HeadOf(bone) + new Vector2(3, -2));
			RestEdits.MoveJoint(frozen, bone, frozen.HeadOf(bone) + new Vector2(6, -4));   // the next mouse move
			RestEdits.Turn(FrozenRest.Capture(skeleton), bone, bone.SetupRotation + 0.05f);
			RestEdits.Stretch(FrozenRest.Capture(skeleton), bone, bone.Length * 1.1f);
		}
		Rest(skeleton);
		StayedPut(skeleton, before, 0.01f);

		// Dragged onto the root in the list (with "parts stay put", it lets go of its parent's end first)
		if (bones.LastOrDefault(b => b.Parent != root) is { } moving)
		{
			RigOps.SetConnected(moving, false);
			Assert.True(RigOps.Reparent(skeleton, moving, root));
			Rest(skeleton);
			StayedPut(skeleton, before, 0.01f);
		}

		// Delete ▾ > keep what's on it: its parts and bones go to its parent
		if (skeleton.AllBones().FirstOrDefault(b => b.Parent != null && b.Children.Count > 0 && skeleton.Slots.Any(slot => slot.Bone == b))
			is { Parent: { } parent } doomed)
		{
			var itsParts = skeleton.Slots.Where(slot => slot.Bone == doomed).ToList();
			Assert.True(RigOps.DissolveBone(skeleton, new List<Animation>(), doomed));
			Rest(skeleton);
			StayedPut(skeleton, before, 0.01f);
			Assert.All(itsParts, slot => Assert.Same(parent, slot.Bone));
		}

		// Bind ▾ > Every part onto its nearest bone
		PartRigging.PartsToNearestBones(skeleton, store, skeleton.Slots);
		Rest(skeleton);
		StayedPut(skeleton, before, 0.01f);
		if (skeleton.AllBones().Any(b => b.Parent != null))
			Assert.DoesNotContain(skeleton.Slots, slot => slot.Bone == root);

		var carrying = skeleton.AllBones().FirstOrDefault(b => b.Parent != null && skeleton.Slots.Any(slot => slot.Bone == b && Vector2.Distance(Placed(skeleton, slot).At, b.World.Translation) > 4f));
		if (carrying != null) TurningMovesWhatsOnIt(skeleton, carrying, before);
	}
}
