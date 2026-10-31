using System.Windows.Input;
using Animator.Viewer.Tools;
using Rig = Animator.Tests.PickingTests.Rig;

namespace Animator.Tests;

// Editing in a skin: skin 0 is the object's own drawings; while another skin is being edited,
// changing a part (placing it, painting it, new drawings, new parts) changes that skin's copy
public class SkinEditingTests
{
	private static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = 1e-2f) =>
		Assert.True(Vector2.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

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

		rig.Drag(0, -100, 0, -80, MouseButton.Left);   // the body, 20 up
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
		rig.Drag(0, -100, 0, -90, MouseButton.Left);
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
