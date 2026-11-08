using Animator.Viewer.Tools;
using Rig = Animator.Tests.PickingTests.Rig;

namespace Animator.Tests;

// The viewport's tools: with Move or Rotate, the select button takes hold of a bone or part that
// way, wherever it's grabbed; elsewhere it still draws a box
public class ViewToolTests
{
	private static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = 1e-2f) =>
		Assert.True(Vector2.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

	[Fact]
	public void The_Move_tool_moves_a_bone_grabbed_by_its_body()
	{
		var rig = new Rig();
		rig.Tool.Tool = ViewTool.Move;
		rig.Drag(30, 100, 30, 120, PointerButton.Left);   // the upper arm's body (the Select tool: it would turn it with the other button)
		rig.Pose();
		AssertNear(new Vector2(0, 120), rig.Upper.World.Translation);
		Assert.Equal(0f, rig.Upper.SetupRotation, 4);
	}

	[Fact]
	public void The_Rotate_tool_turns_a_bone_grabbed_by_its_joint_and_a_part_round_its_pivot()
	{
		var rig = new Rig();
		rig.Tool.Tool = ViewTool.Rotate;
		// The forearm's joint (60, 100) turns round itself: grabbed a little off it, then swung up
		rig.Tool.MouseDown(Rig.Screen(64, 100), PointerButton.Left);
		foreach (var degrees in new[] { 30, 60, 90 })
		{
			float a = degrees * MathF.PI / 180;
			rig.Tool.MouseMove(Rig.Screen(60 + 4 * MathF.Cos(a), 100 + 4 * MathF.Sin(a)));
			rig.Pose();
		}
		rig.Tool.MouseUp();
		Assert.Equal(MathF.PI / 2, rig.Lower.SetupRotation, 2);
		AssertNear(new Vector2(60, 100), rig.Lower.World.Translation);

		// The body, turned a quarter round its middle
		rig.Tool.MouseDown(Rig.Screen(8, -100), PointerButton.Left);
		foreach (var degrees in new[] { 30, 60, 90 })
		{
			float a = degrees * MathF.PI / 180;
			rig.Tool.MouseMove(Rig.Screen(8 * MathF.Cos(a), -100 + 8 * MathF.Sin(a)));
			rig.Pose();
		}
		rig.Tool.MouseUp();
		Assert.Equal(MathF.PI / 2, rig.Skeleton.ResolveAttachment(rig.Body)!.Rotation, 2);
		AssertNear(new Vector2(0, -100), rig.CentreOf(rig.Body));
	}

	[Fact]
	public void With_a_tool_a_drag_on_empty_space_still_picks_in_a_box()
	{
		var rig = new Rig();
		rig.Tool.Tool = ViewTool.Move;
		rig.Drag(-20, 120, 80, 40, PointerButton.Left);
		Assert.Equal(3, rig.State.Picked.Count);   // the arm's joints and the hand
		Assert.Equal(new Vector2(60, 0), rig.Lower.SetupPosition);   // nothing moved
	}

	[Fact]
	public void The_Select_tool_only_selects_its_other_button_pans()
	{
		var rig = new Rig();
		rig.Tool.SelectTransforms = false;   // (as in the editor)
		var pan = rig.Camera.Pan;
		rig.Drag(30, 100, 30, 140, PointerButton.Right);   // the upper arm's body
		rig.Pose();
		AssertNear(new Vector2(0, 100), rig.Upper.World.Translation);
		Assert.Equal(0f, rig.Upper.SetupRotation, 4);
		Assert.NotEqual(pan, rig.Camera.Pan);
	}

	[Fact]
	public void A_joint_dragged_with_the_parts_staying_put_takes_its_whole_bone_along_and_Shift_just_the_joint()
	{
		var rig = new Rig();
		rig.Tool.KeepPartsInPlace = true;
		rig.Tool.Tool = ViewTool.Move;
		rig.Drag(60, 100, 60, 120, PointerButton.Left);   // the forearm's joint
		rig.Pose();
		AssertNear(new Vector2(60, 120), rig.Lower.World.Translation);
		Assert.Equal(40f, rig.Lower.Length, 2);
		Assert.Equal(0f, rig.Lower.WorldRotation, 3);

		var other = new Rig();
		other.Tool.KeepPartsInPlace = true;
		other.Tool.Tool = ViewTool.Move;
		other.Held = KeyMods.Shift;
		other.Drag(60, 100, 60, 120, PointerButton.Left);
		other.Pose();
		AssertNear(new Vector2(100, 100), other.Lower.TipWorld, 0.05f);   // its end stayed
		Assert.Equal(MathF.Sqrt(40 * 40 + 20 * 20), other.Lower.Length, 1);
	}
}
