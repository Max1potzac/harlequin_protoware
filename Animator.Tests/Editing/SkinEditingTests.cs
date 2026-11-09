using Animator.Viewer.Tools;
using Rig = Animator.Tests.PickingTests.Rig;

namespace Animator.Tests;

// Editing in a skin: skin 0 is the object's own drawings; while another skin is being edited,
// changing a part (placing it, painting it, new drawings, new parts) changes that skin's copy
public class SkinEditingTests
{
	private static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = 1e-2f) =>
		Assert.True(Vector2.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

	// A skin's own part: a skirt (the skin's waist) in front of the legs, where the object's own
	// waist stays behind them; keyed as the waist is, and only while the skin is on
	[Fact]
	public void A_skin_stacks_its_drawings_apart_on_a_part_of_its_own()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 10 };
		skeleton.Roots.Add(root);
		Asset Picture(string name)
		{
			var a = new Asset { Name = name, Width = 10, Height = 10 };
			a.Layers.Add(new AssetLayer { Name = "Base" });
			skeleton.Assets[a.Id] = a;
			return a;
		}
		Slot Part(string name)
		{
			var slot = new Slot(name, root) { SetupAttachment = name };
			skeleton.Slots.Add(slot);
			skeleton.DefaultSkin.Set(name, new Attachment { Name = name, AssetId = Picture(name).Id });
			return slot;
		}
		var waist = Part("waist");
		var leg = Part("leg");
		var outfit = SkinOps.MakeSkin(skeleton, "skirt", category: "Body");
		SkinOps.Show(skeleton, outfit, on: false);
		var skirt = new Attachment { Name = "waist", AssetId = Picture("skirt").Id };
		outfit.Set("waist", skirt);
		var walk = new Animation { Name = "walk", Duration = 1 };
		var swap = new AttachmentTimeline("waist");
		swap.Add(0.5f, null);   // (gone halfway)
		walk.Timelines.Add(swap);
		walk.GetOrCreateDrawOrder().Add(0, new[] { "leg", "waist" });
		var animations = new List<Animation> { walk };

		var own = SkinOps.OwnPart(skeleton, animations, outfit, waist)!;
		Assert.Equal("waist skirt", own.Name);
		Assert.Same(root, own.Bone);
		Assert.Equal(new[] { "waist", "waist skirt", "leg" }, skeleton.Slots.Select(s => s.Name));
		Assert.Contains("waist", outfit.Hides);
		Assert.Same(skirt, outfit.Get("waist skirt", "waist"));
		Assert.Null(outfit.Get("waist", "waist"));
		Assert.NotNull(walk.Find<AttachmentTimeline>("waist skirt"));
		Assert.Equal(new[] { "leg", "waist", "waist skirt" }, walk.DrawOrder!.Track.Keys[0].Value);
		Assert.Same(own, SkinOps.OwnPart(skeleton, animations, outfit, waist));   // (made once)
		Assert.Null(SkinOps.OwnPart(skeleton, animations, outfit, leg));          // (no drawings of its own there)

		// Off, the object's own waist; on, the skirt (keyed as the waist is), and no waist
		skeleton.SetToSetupPose();
		Assert.Null(skeleton.ResolveAttachment(own));
		SkinOps.Show(skeleton, outfit);
		skeleton.SetToSetupPose();
		Assert.Same(skirt, skeleton.ResolveAttachment(own));
		Assert.Null(skeleton.ResolveAttachment(waist));
		walk.Apply(skeleton, 0.75f);
		Assert.Null(skeleton.ResolveAttachment(own));
	}

	[Fact]
	public void A_skins_own_copy_is_made_from_what_shows_and_turns_the_skin_on()
	{
		var rig = new Rig();
		var skeleton = rig.Skeleton;
		var own = skeleton.DefaultSkin.Get("body", "body")!;
		var burnt = SkinOps.MakeSkin(skeleton, "Burnt");
		SkinOps.Show(skeleton, burnt, on: false);
		rig.Pose();

		var copy = SkinOps.Own(skeleton, burnt, rig.Body)!;
		Assert.NotSame(own, copy);
		Assert.Equal((own.Name, own.AssetId, own.Offset), (copy.Name, copy.AssetId, copy.Offset));
		Assert.Contains(burnt, skeleton.ShownSkins);
		Assert.Same(copy, skeleton.ResolveAttachment(rig.Body));
		Assert.Same(copy, SkinOps.Own(skeleton, burnt, rig.Body));   // made once
		Assert.True(SkinOps.SharesPixels(skeleton, copy));            // (painting it copies the pixels first)
		Assert.Equal(new[] { rig.Body }, SkinOps.PartsOf(skeleton, burnt));

		// A new drawing while editing it goes into it too
		var pixels = new Asset { Name = "body burnt 2", Width = 20, Height = 20 };
		var drawing = DrawingOps.AddDrawing(skeleton, rig.Body, copy, pixels, into: burnt);
		Assert.Same(drawing, burnt.Get("body", drawing.Name));
		Assert.Null(skeleton.DefaultSkin.Get("body", drawing.Name));
	}

	[Fact]
	public void Moving_a_part_while_editing_a_skin_moves_only_the_skins_copy()
	{
		var rig = new Rig();
		var burnt = SkinOps.MakeSkin(rig.Skeleton, "Burnt");
		rig.State.EditSkin = burnt;
		rig.Pose();
		rig.Tool.Tool = ViewTool.Move;

		rig.Drag(0, -100, 0, -80, PointerButton.Left);   // the body, 20 up
		rig.Pose();

		AssertNear(new Vector2(0, -80), rig.CentreOf(rig.Body));
		AssertNear(new Vector2(0, -100), rig.Skeleton.DefaultSkin.Get("body", "body")!.Offset);   // skin 0 as it was
		Assert.NotNull(burnt.Get("body", "body"));

		SkinOps.Show(rig.Skeleton, burnt, on: false);   // the skin off: as the object is by default
		AssertNear(new Vector2(0, -100), rig.CentreOf(rig.Body));
		SkinOps.Show(rig.Skeleton, burnt);

		rig.State.Undo.Undo();   // one step: the move and the copy
		Assert.Null(burnt.Get("body", "body"));

		// Editing skin 0 again: the object's own drawing moves
		rig.State.EditSkin = null;
		rig.Drag(0, -100, 0, -90, PointerButton.Left);
		rig.Pose();
		AssertNear(new Vector2(0, -90), rig.CentreOf(rig.Body));
		Assert.Null(burnt.Get("body", "body"));
	}

	[Fact]
	public void The_skin_being_edited_is_forgotten_when_it_is_deleted()
	{
		var rig = new Rig();
		var burnt = SkinOps.MakeSkin(rig.Skeleton, "Burnt");
		rig.State.EditSkin = burnt;
		SkinOps.Dissolve(rig.Skeleton, burnt);
		Assert.Null(rig.State.EditSkin);
		Assert.Same(rig.Skeleton.DefaultSkin.Get("body", "body"), rig.State.EditableDrawing(rig.Body));
	}
}
