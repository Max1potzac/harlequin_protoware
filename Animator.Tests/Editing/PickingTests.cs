using System.Text.Json;
using System.Windows.Input;
using Animator.Core.Serialization;
using Animator.Viewer;
using Animator.Viewer.Panels;
using Animator.Viewer.Rendering;
using Animator.Viewer.Tools;
using SkiaSharp;

namespace Animator.Tests;

// Milestone 14: picking several bones and parts (clicks, boxes, Ctrl and Shift), dragging them
// together with the transform button, dragging selected keys instead of keying the playhead, and
// hiding things while editing
public class PickingTests
{
	// A root bone, an arm of two bones off to one side, a part on the root and one on the forearm,
	// in a 400×400 viewport whose middle is the world's origin (y up):
	//   root: joint (0, 0), pointing right, 40 long        body (on the root): 20×20 around (0, -100)
	//   upper: joint (0, 100), 40 long                     hand (on the forearm): 20×20 around (60, 50)
	//   lower: joint (60, 100), 40 long (on the upper arm, past its end)
	internal sealed class Rig   // (the step 15 tests use it too)
	{
		public readonly AssetStore Store = new();
		public readonly Skeleton Skeleton = new();
		public readonly Animation Animation = new() { Name = "walk", Duration = 1f };
		public readonly Bone Root, Upper, Lower;
		public readonly Slot Body, Hand;
		public readonly EditorState State;
		public readonly Camera Camera = new() { ViewSize = new Vector2(400, 400) };
		public readonly SkeletonRenderer Renderer;
		public readonly BoneTool Tool;
		public ModifierKeys Held = ModifierKeys.None;

		public Rig()
		{
			Root = new Bone { Name = "root", Length = 40 };
			Skeleton.Roots.Add(Root);
			Upper = new Bone { Name = "upper", Length = 40, SetupPosition = new Vector2(0, 100) };
			Root.AddChild(Upper);
			Lower = new Bone { Name = "lower", Length = 40, SetupPosition = new Vector2(60, 0) };
			Upper.AddChild(Lower);
			Body = AddPart("body", Root, new Vector2(0, -100));
			Hand = AddPart("hand", Lower, new Vector2(0, -50));

			State = new EditorState(Skeleton, Animation) { Playing = false };
			Renderer = new SkeletonRenderer(Store) { HideHidden = true };
			Tool = new BoneTool(State, Camera, Renderer) { KeepPartsInPlace = false, Modifiers = () => Held };
			Pose();
		}

		public Slot AddPart(string name, Bone bone, Vector2 offset)
		{
			var asset = new Asset { Name = name, Width = 20, Height = 20 };
			using (var pixels = new SKBitmap(AssetStore.InfoFor(20, 20)))
			{
				pixels.Erase(SKColors.White);
				Store.AddLayer(asset, "Base", pixels);
			}
			Skeleton.Assets[asset.Id] = asset;
			Skeleton.DefaultSkin.Set(name, new Attachment { Name = name, AssetId = asset.Id, Offset = offset });
			var slot = new Slot(name, bone) { SetupAttachment = name };
			Skeleton.Slots.Add(slot);
			return slot;
		}

		// What the editor does every frame: the rig posed from the animation at the playhead
		public void Pose() => IkMatching.Evaluate(Skeleton, Animation, State.Time);

		public static Vector2 Screen(float x, float y) => new(200 + x, 200 - y);

		public void Click(float x, float y, MouseButton button = MouseButton.Left)
		{
			Tool.MouseDown(Screen(x, y), button);
			Tool.MouseUp();
		}

		// A drag in a few steps, with frames going by in between
		public void Drag(float x0, float y0, float x1, float y1, MouseButton button)
		{
			Tool.MouseDown(Screen(x0, y0), button);
			for (int i = 1; i <= 4; i++)
			{
				float t = i / 4f;
				Tool.MouseMove(Screen(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t));
				Pose();
			}
			Tool.MouseUp();
		}

		public Vector2 CentreOf(Slot slot)
		{
			var drawing = Skeleton.ResolveAttachment(slot)!;
			return Vector2.Transform(new Vector2(10, 10), SkeletonRenderer.ImageToWorld(slot, drawing, 20, 20));
		}
	}

	private static string[] Names(IEnumerable<object> items) =>
		items.Select(i => i switch { Bone b => b.Name, Slot s => s.Name, _ => "?" }).OrderBy(n => n, StringComparer.Ordinal).ToArray();

	private static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = 1e-3f) =>
		Assert.True(Vector2.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

	// ======================= picking =======================

	[Fact]
	public void A_click_picks_one_thing_Ctrl_toggles_and_Shift_adds()
	{
		var rig = new Rig();
		rig.Click(0, 100);   // the upper arm's joint
		Assert.Equal(new object[] { rig.Upper }, rig.State.Picked);

		rig.Held = ModifierKeys.Control;
		rig.Click(60, 100);   // the forearm's joint: picked as well
		Assert.Equal(new object[] { rig.Upper, rig.Lower }, rig.State.Picked);
		Assert.Same(rig.Lower, rig.State.Selected);
		rig.Click(0, 100);    // the upper arm again: let go
		Assert.Equal(new object[] { rig.Lower }, rig.State.Picked);

		rig.Held = ModifierKeys.Shift;
		rig.Click(0, -100);   // the body's pixels
		Assert.Equal(new object[] { rig.Lower, rig.Body }, rig.State.Picked);
		Assert.Same(rig.Body, rig.State.SelectedSlot);

		rig.Held = ModifierKeys.None;
		rig.Click(150, 150);  // nothing there: nothing picked
		Assert.Empty(rig.State.Picked);
		Assert.Null(rig.State.Selected);
	}

	[Fact]
	public void A_box_picks_the_bones_and_parts_in_it_and_Ctrl_lets_them_go()
	{
		var rig = new Rig();
		rig.Drag(-20, 120, 80, 40, MouseButton.Left);   // the arm's joints and the hand, not the root or the body
		Assert.Equal(new[] { "hand", "lower", "upper" }, Names(rig.State.Picked));
		Assert.Null(rig.Tool.SelectionBox);              // gone once it's let go

		rig.Held = ModifierKeys.Control;
		rig.Drag(40, 70, 80, 30, MouseButton.Left);      // just the hand
		Assert.Equal(new[] { "lower", "upper" }, Names(rig.State.Picked));

		rig.Held = ModifierKeys.Shift;
		rig.Drag(-20, -80, 20, -120, MouseButton.Left);  // the body, too
		Assert.Equal(new[] { "body", "lower", "upper" }, Names(rig.State.Picked));
	}

	[Fact]
	public void Hidden_bones_and_parts_cant_be_picked()
	{
		var rig = new Rig();
		rig.Upper.Hidden = true;   // and with it the forearm, and the hand on that
		Assert.True(rig.Lower.IsHidden);
		Assert.True(rig.Hand.IsHidden);

		rig.Click(60, 100);
		Assert.Empty(rig.State.Picked);
		rig.Drag(-200, 200, 200, -200, MouseButton.Left);   // a box over everything
		Assert.Equal(new[] { "body", "root" }, Names(rig.State.Picked));
	}

	[Fact]
	public void Selecting_one_thing_on_its_own_lets_the_others_go_unless_it_was_picked()
	{
		var rig = new Rig();
		var state = rig.State;
		state.Pick(new object[] { rig.Upper, rig.Lower, rig.Hand });
		Assert.Same(rig.Hand, state.SelectedSlot);
		Assert.Same(rig.Lower, state.Selected);   // a part's bone is the selected bone

		state.SelectedSlot = null;   // a panel shows one of the picked: they stay picked
		state.Selected = rig.Upper;
		Assert.Equal(3, state.Picked.Count);

		state.Selected = rig.Root;   // something else: just that
		Assert.Equal(new object[] { rig.Root }, state.Picked);
		state.Selected = rig.Upper;  // and the others don't come back
		Assert.Equal(new object[] { rig.Upper }, state.Picked);
	}

	// ======================= dragging =======================

	[Fact]
	public void The_transform_button_drags_a_joint_and_the_select_button_doesnt()
	{
		var rig = new Rig();
		rig.Drag(60, 100, 70, 110, MouseButton.Left);   // a box: nothing moves
		Assert.Equal(new Vector2(60, 0), rig.Lower.SetupPosition);

		rig.Drag(60, 100, 70, 110, MouseButton.Right);
		AssertNear(new Vector2(70, 10), rig.Lower.SetupPosition);
		Assert.Same(rig.Lower, rig.State.Selected);

		rig.State.Undo.Undo();
		AssertNear(new Vector2(60, 0), rig.Lower.SetupPosition);
	}

	[Fact]
	public void Dragging_one_of_several_picked_keys_them_all_at_the_playhead()
	{
		var rig = new Rig();
		rig.State.AnimateMode = true;
		rig.State.SetFrame(10);
		rig.Pose();
		rig.State.Pick(new object[] { rig.Upper, rig.Body });

		rig.Drag(0, 100, 10, 95, MouseButton.Right);   // by the upper arm's joint

		var arm = rig.Animation.Find<TranslateTimeline>("upper")!;
		var key = Assert.Single(arm.Track.Keys);
		Assert.Equal(10, (int)MathF.Round(key.Time * 30));
		AssertNear(new Vector2(10, -5), key.Value);
		AssertNear(new Vector2(10, -5), Assert.Single(rig.Animation.Find<PartTranslateTimeline>("body")!.Track.Keys).Value);
		Assert.Null(rig.Animation.Find<TranslateTimeline>("lower"));   // it rides on the upper arm
		Assert.Null(rig.Animation.Find<PartTranslateTimeline>("hand"));

		rig.State.Undo.Undo();
		Assert.Null(rig.Animation.Find<TranslateTimeline>("upper"));
		Assert.Null(rig.Animation.Find<PartTranslateTimeline>("body"));
	}

	[Fact]
	public void With_their_keys_selected_a_drag_moves_those_keys_and_keys_nothing_new()
	{
		var rig = new Rig();
		var arm = new TranslateTimeline("upper");
		arm.Track.Add(0f, Vector2.Zero);
		arm.Track.Add(20 / 30f, new Vector2(0, 30));
		var body = new PartTranslateTimeline("body");
		body.Track.Add(0f, Vector2.Zero);
		body.Track.Add(20 / 30f, new Vector2(5, 0));
		var turn = new RotateTimeline("lower");   // something else's key: left alone
		turn.Track.Add(0f, 0.5f);
		rig.Animation.Timelines.AddRange(new Timeline[] { arm, body, turn });

		rig.State.AnimateMode = true;
		rig.State.Pick(new object[] { rig.Upper, rig.Body });
		Assert.Equal(4, new TimelinePanel(rig.State).SelectKeysOf(rig.State.PickedBones, rig.State.PickedSlots));
		rig.State.SetFrame(10);
		rig.Pose();   // halfway between the keys: the upper arm's joint is at (0, 115)
		AssertNear(new Vector2(0, 115), rig.Upper.World.Translation);

		rig.Drag(0, 115, 20, 115, MouseButton.Right);

		Assert.Equal(2, arm.Track.Keys.Count);
		AssertNear(new Vector2(20, 0), arm.Track.Keys[0].Value);
		AssertNear(new Vector2(20, 30), arm.Track.Keys[1].Value);
		Assert.Equal(2, body.Track.Keys.Count);
		AssertNear(new Vector2(20, 0), body.Track.Keys[0].Value);
		AssertNear(new Vector2(25, 0), body.Track.Keys[1].Value);
		Assert.Equal(0.5f, Assert.Single(turn.Track.Keys).Value);
	}

	[Fact]
	public void A_bone_turned_with_its_keys_selected_turns_them_by_the_whole_angle()
	{
		var rig = new Rig();
		var turn = new RotateTimeline("upper");
		turn.Track.Add(0f, 0f);   // where the playhead is, not selected: the pose here won't follow the others
		turn.Track.Add(20 / 30f, 0.2f);
		turn.Track.Add(1f, 0.4f);
		rig.Animation.Timelines.Add(turn);
		rig.State.AnimateMode = true;
		rig.State.SelectedKeys.Add((turn, 20));
		rig.State.SelectedKeys.Add((turn, 30));
		rig.Pose();

		// Round the upper arm's joint (0, 100), from along the bone to straight up, frames going by
		rig.Tool.MouseDown(Rig.Screen(30, 100), MouseButton.Right);
		foreach (var degrees in new[] { 30, 60, 90 })
		{
			float a = degrees * MathF.PI / 180;
			rig.Tool.MouseMove(Rig.Screen(30 * MathF.Cos(a), 100 + 30 * MathF.Sin(a)));
			rig.Pose();
		}
		rig.Tool.MouseUp();

		var values = turn.Track.Keys.Select(k => k.Value).ToArray();
		Assert.Equal(0f, values[0], 4);
		Assert.Equal(0.2f + MathF.PI / 2, values[1], 3);
		Assert.Equal(0.4f + MathF.PI / 2, values[2], 3);
	}

	[Fact]
	public void In_Setup_mode_picked_bones_move_together_and_the_parts_stay_put()
	{
		var rig = new Rig();
		rig.Tool.KeepPartsInPlace = true;
		var hand = rig.CentreOf(rig.Hand);
		rig.State.Pick(new object[] { rig.Upper, rig.Lower });

		rig.Drag(0, 100, 0, 120, MouseButton.Right);   // by the upper arm's joint
		rig.Pose();

		AssertNear(new Vector2(0, 120), rig.Upper.World.Translation);
		AssertNear(new Vector2(60, 120), rig.Lower.World.Translation);
		AssertNear(hand, rig.CentreOf(rig.Hand));   // on the forearm, but not picked: it stays
		AssertNear(new Vector2(0, -100), rig.CentreOf(rig.Body));

		rig.State.Undo.Undo();
		rig.Pose();
		AssertNear(new Vector2(0, 100), rig.Upper.World.Translation);
		AssertNear(new Vector2(60, 100), rig.Lower.World.Translation);
	}

	[Fact]
	public void Picked_parts_turn_together_each_round_its_own_pivot()
	{
		var rig = new Rig();
		rig.State.Pick(new object[] { rig.Body, rig.Hand });   // the hand is the one with handles

		// The hand's round handle sits above it, at (60, 88); turned a quarter round its middle (60, 50)
		rig.Tool.MouseDown(Rig.Screen(60, 88), MouseButton.Right);
		foreach (var degrees in new[] { 120, 150, 180 })
		{
			float a = degrees * MathF.PI / 180;
			rig.Tool.MouseMove(Rig.Screen(60 + 38 * MathF.Cos(a), 50 + 38 * MathF.Sin(a)));
			rig.Pose();
		}
		rig.Tool.MouseUp();
		rig.Pose();

		Assert.Equal(MathF.PI / 2, rig.Skeleton.ResolveAttachment(rig.Hand)!.Rotation, 3);
		Assert.Equal(MathF.PI / 2, rig.Skeleton.ResolveAttachment(rig.Body)!.Rotation, 3);
		AssertNear(new Vector2(60, 50), rig.CentreOf(rig.Hand));
		AssertNear(new Vector2(0, -100), rig.CentreOf(rig.Body));
	}

	[Fact]
	public void In_the_scene_the_select_button_picks_and_the_transform_button_moves()
	{
		var rig = new Rig();
		var scene = new Scene { Fps = 30 };
		var character = new SceneObject("Character", ObjectKind.Rigged, rig.Skeleton);
		scene.Objects.Add(character);
		var tool = new ObjectTool(() => scene, new SceneState(), rig.Camera, rig.Renderer, rig.State.Undo) { Modifiers = () => ModifierKeys.None };

		tool.MouseDown(Rig.Screen(0, -100), MouseButton.Left, 1);   // its body
		tool.MouseUp();
		Assert.Same(character, tool.Selected);

		tool.MouseDown(Rig.Screen(0, -100), MouseButton.Left, 1);   // picking doesn't move it
		tool.MouseMove(Rig.Screen(30, -100));
		tool.MouseUp();
		Assert.Equal(Vector2.Zero, character.Position);

		tool.MouseDown(Rig.Screen(0, -100), MouseButton.Right, 1);
		tool.MouseMove(Rig.Screen(15, -100));
		tool.MouseMove(Rig.Screen(30, -100));
		tool.MouseUp();
		AssertNear(new Vector2(30, 0), character.Position);
		rig.State.Undo.Undo();
		AssertNear(Vector2.Zero, character.Position);

		tool.MouseDown(Rig.Screen(150, 150), MouseButton.Left, 1);   // nothing there
		tool.MouseUp();
		Assert.Null(tool.Selected);
	}

	[Fact]
	public void Picked_parts_that_show_nothing_at_this_frame_move_too()
	{
		// An effect between its bursts: picked (Ctrl+A picks it), but nothing of it to see here
		var rig = new Rig();
		var spark = rig.AddPart("spark", rig.Root, new Vector2(100, -100));
		spark.SetupAttachment = null;
		rig.Pose();
		Assert.Null(rig.Skeleton.ResolveAttachment(spark));

		// Animate mode: it gets a placement key like the others
		rig.State.AnimateMode = true;
		rig.State.Pick(new object[] { rig.Body, spark });
		rig.Drag(0, -100, 20, -100, MouseButton.Right);   // by the body
		AssertNear(new Vector2(20, 0), Assert.Single(rig.Animation.Find<PartTranslateTimeline>("spark")!.Track.Keys).Value);

		// Setup mode: its drawing moves, though it isn't shown at rest
		rig.State.AnimateMode = false;
		rig.Animation.Timelines.Clear();
		rig.Pose();
		rig.Drag(0, -100, 0, -130, MouseButton.Right);
		var drawing = rig.Skeleton.DefaultSkin.Get("spark", "spark")!;
		AssertNear(new Vector2(100, -130), drawing.Offset);
		AssertNear(new Vector2(0, -130), rig.Skeleton.DefaultSkin.Get("body", "body")!.Offset);
	}

	// ======================= hiding =======================

	[Fact]
	public void Hiding_everything_else_keeps_what_the_picked_hang_from()
	{
		var rig = new Rig();
		Assert.True(VisibilityOps.HideOthers(rig.Skeleton, new object[] { rig.Hand }));
		Assert.False(rig.Hand.IsHidden);
		Assert.False(rig.Lower.IsHidden || rig.Upper.IsHidden || rig.Root.IsHidden);   // what it hangs from
		Assert.True(rig.Body.IsHidden);   // another part on one of those

		Assert.True(VisibilityOps.ShowAll(rig.Skeleton));
		Assert.False(rig.Body.IsHidden);
		Assert.False(VisibilityOps.ShowAll(rig.Skeleton));   // nothing left to show

		// A kept bone keeps what's on it and under it
		Assert.True(VisibilityOps.HideOthers(rig.Skeleton, new object[] { rig.Upper }));
		Assert.False(rig.Lower.IsHidden || rig.Hand.IsHidden || rig.Root.IsHidden);
		Assert.True(rig.Body.IsHidden);

		// And undo puts it back as it was
		var before = EditAidsSnapshot.Capture(rig.Skeleton);
		VisibilityOps.SetHidden(new object[] { rig.Root }, true);
		Assert.True(rig.Hand.IsHidden);
		before.Restore();
		Assert.False(rig.Hand.IsHidden);
		Assert.True(rig.Body.IsHidden);
	}

	[Fact]
	public void What_is_hidden_is_saved_and_loaded()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root" };
		skeleton.Roots.Add(root);
		var arm = new Bone { Name = "arm" };
		root.AddChild(arm);
		var hand = new Bone { Name = "hand" };
		arm.AddChild(hand);
		skeleton.Slots.Add(new Slot("body", root) { Hidden = true });
		skeleton.Slots.Add(new Slot("glove", hand));
		arm.Hidden = true;

		var dto = ProjectMapper.ToDto(skeleton, Array.Empty<Animation>(), 30);
		Assert.Null(dto.Bones.Single(b => b.Name == "root").Hidden);   // shown: nothing in the file
		string json = JsonSerializer.Serialize(dto, ProjectMapper.Json);
		var (loaded, _) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(json, ProjectMapper.Json)!);

		Assert.False(loaded.FindBone("root")!.Hidden);
		Assert.True(loaded.FindBone("arm")!.Hidden);
		Assert.False(loaded.FindBone("hand")!.Hidden);
		Assert.True(loaded.FindBone("hand")!.IsHidden);   // under the hidden arm
		Assert.True(loaded.FindSlot("body")!.Hidden);
		Assert.True(loaded.FindSlot("glove")!.IsHidden);
	}
}
