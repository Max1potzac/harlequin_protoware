using System.Numerics;
using Animator.Viewer;
using Animator.Viewer.Rendering;
using Animator.Viewer.Tools;
using Rig = Animator.Tests.PickingTests.Rig;

namespace Animator.Tests;

// The viewport drags that left the window (GuideTool, MeshEditTool, EditorSession's box drags),
// and the IK, space and throw edits (EditorSession.Ik.cs)
public class ViewportDragTests
{
	private static EditorSession SessionOf(Rig rig) => new(rig.Store, rig.State);

	[Fact]
	public void A_guide_comes_out_of_a_ruler_moves_and_goes_back_in()
	{
		var rig = new Rig();
		var session = SessionOf(rig);
		var guides = new GuideTool(session, rig.Camera) { Snap = new Snapping { ToGrid = true } };
		var scene = session.Scene;

		// Out of the top ruler: a guide across, dragged down into the view, snapping to the grid
		Assert.Equal(GuideTool.Press.NewGuide, guides.MouseDown(new Vector2(200, 5)));
		var guide = Assert.Single(scene.Guides);
		Assert.False(guide.Vertical);
		guides.MouseMove(new Vector2(200, 101));
		Assert.Equal(0f, MathF.IEEERemainder(guide.At, scene.GridSize), 3);
		Assert.True(guides.MouseUp(new Vector2(200, 101)));
		Assert.Null(guides.Dragging);
		Assert.Same(guide, guides.GuideAt(new Vector2(50, rig.Camera.WorldToScreen(new Vector2(0, guide.At)).Y)));

		// Its mark on the left ruler... (none: it runs along the top one's way)
		var (top, left) = guides.Marks();
		Assert.Empty(top);
		Assert.Single(left);

		// The rulers' corner takes the press and does nothing; elsewhere, nothing's there
		Assert.Equal(GuideTool.Press.Taken, guides.MouseDown(new Vector2(2, 2)));
		Assert.False(guides.MouseUp(new Vector2(2, 2)));
		Assert.Equal(GuideTool.Press.NotMine, guides.MouseDown(new Vector2(300, 300)));

		// Dragged back onto the ruler: gone, one undo step; undo brings it back
		var at = rig.Camera.WorldToScreen(new Vector2(0, guide.At));
		Assert.Equal(GuideTool.Press.Taken, guides.MouseDown(new Vector2(250, at.Y)));
		Assert.True(guides.MouseUp(new Vector2(250, 5)));
		Assert.Empty(scene.Guides);
		rig.State.Undo.Undo();
		Assert.Single(scene.Guides);

		// Locked: they can't be taken hold of
		guides.LockGuides = true;
		Assert.Null(guides.GuideAt(new Vector2(50, at.Y)));
	}

	[Fact]
	public void A_mesh_point_is_dragged_as_one_undo_step()
	{
		var rig = new Rig();
		var session = SessionOf(rig);
		rig.State.SelectedSlot = rig.Body;
		Assert.True(session.MakeMesh(rig.Body, 2));
		var (slot, drawing, mesh) = session.MeshOf(rig.Body)!.Value;
		var start = mesh!.Points.ToArray();

		var tool = new MeshEditTool(session, rig.Camera);
		var corner = rig.Camera.WorldToScreen(MeshOps.Posed(slot, drawing, mesh)[0]);
		Assert.False(tool.MouseDown(corner, PointerButton.Left, subtract: false));   // the tool's off
		tool.Mode = MeshMode.Points;
		tool.MouseMove(corner);
		Assert.Equal(0, tool.Hover);
		Assert.False(tool.MouseDown(corner, PointerButton.Right, subtract: false));
		Assert.True(tool.MouseDown(corner, PointerButton.Left, subtract: false));
		Assert.True(tool.Dragging);
		tool.MouseMove(corner + new Vector2(6, 0));
		Assert.True(tool.MouseUp());
		Assert.NotEqual(start[0], drawing.Mesh!.Points[0]);
		rig.State.Undo.Undo();
		Assert.Equal(start[0], session.MeshOf(rig.Body)!.Value.Mesh!.Points[0]);

		// Weights need a weighted mesh and a bone
		tool.Mode = MeshMode.Weights;
		Assert.False(tool.MouseDown(corner, PointerButton.Left, subtract: false));
	}

	[Fact]
	public void A_hitbox_is_dragged_and_resized_through_the_session()
	{
		var rig = new Rig();
		var session = SessionOf(rig);
		rig.State.Selected = rig.Root;
		var box = Assert.IsType<Hitbox>(session.AddHitbox(HitboxShape.Box, "hurt"));
		rig.Skeleton.UpdateWorld();
		var offset = box.Offset;

		var from = Vector2.Transform(Vector2.Zero, box.World);
		session.BeginBoxDrag(box, resize: false, from);
		Assert.True(session.DraggingBox);
		session.DragBoxTo(from + new Vector2(10, 0));
		Assert.True(session.EndBoxDrag());
		Assert.False(session.DraggingBox);
		Assert.NotEqual(offset, box.Offset);
		rig.State.Undo.Undo();
		Assert.Equal(offset, rig.Skeleton.Hitboxes.Single().Offset);

		// Taken hold of and let go where it was: nothing to undo
		int steps = rig.State.Undo.UndoCount;
		session.BeginBoxDrag(rig.Skeleton.Hitboxes.Single(), resize: true, from);
		Assert.False(session.EndBoxDrag());
		Assert.Equal(steps, rig.State.Undo.UndoCount);
	}

	[Fact]
	public void IK_is_switched_flipped_and_matched_through_the_session()
	{
		var rig = new Rig();
		var session = SessionOf(rig);
		Assert.Null(session.ActiveIk());
		Assert.False(session.ToggleIkMode());

		var target = new Bone { Name = "target", SetupPosition = new Vector2(80, 60) };
		rig.Root.AddChild(target);
		var ik = new IkConstraint { Name = "arm", Target = target };
		ik.Bones.Add(rig.Upper);
		ik.Bones.Add(rig.Lower);
		rig.Skeleton.IkConstraints.Add(ik);
		rig.State.Selected = rig.Lower;
		Assert.Same(ik, session.ActiveIk());

		Assert.True(session.ToggleIkMode());
		Assert.Equal(0f, ik.SetupMix);
		int bend = ik.BendDirection;
		Assert.True(session.FlipIkBend());
		Assert.Equal(-bend, ik.BendDirection);
		rig.State.Undo.Undo();
		rig.State.Undo.Undo();
		Assert.Equal((1f, bend), (ik.SetupMix, ik.BendDirection));

		Assert.False(session.SwitchIkHere());   // Setup: a keyed switch needs Animate
		Assert.True(session.MatchIk(bake: false));
		Assert.False(session.SwitchSpace(rig.Hand.Bone, "root", "root"));   // Setup
		Assert.False(session.Throw(rig.Lower, new ThrowSettings()));       // Setup
	}
}
