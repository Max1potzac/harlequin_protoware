using System.Windows.Input;
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
		rig.Drag(30, 100, 30, 120, MouseButton.Left);   // the upper arm's body (the Select tool: it would turn it with the other button)
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
		rig.Tool.MouseDown(Rig.Screen(64, 100), MouseButton.Left);
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
		rig.Tool.MouseDown(Rig.Screen(8, -100), MouseButton.Left);
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
		rig.Drag(-20, 120, 80, 40, MouseButton.Left);
		Assert.Equal(3, rig.State.Picked.Count);   // the arm's joints and the hand
		Assert.Equal(new Vector2(60, 0), rig.Lower.SetupPosition);   // nothing moved
	}
}
