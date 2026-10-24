using System.Text.Json;
using Animator.Core.Serialization;

namespace Animator.Tests;

// Milestone 13: the order parts are drawn in, the way layers stack in any editor: rearranged
// whatever bones they're on, and keyed in animations (a hand going in front of the body to attack)
public class DrawOrderTests
{
	private const int Fps = 30;

	// Two bones with two parts each, drawn back to front: a1, b1, a2, b2
	private static (Skeleton Skeleton, Slot A1, Slot B1, Slot A2, Slot B2) Rig()
	{
		var skeleton = new Skeleton();
		var a = new Bone { Name = "a", Length = 40 };
		var b = new Bone { Name = "b", Length = 40 };
		skeleton.Roots.Add(a);
		skeleton.Roots.Add(b);
		Slot Part(string name, Bone bone)
		{
			var slot = new Slot(name, bone) { SetupAttachment = name };
			skeleton.Slots.Add(slot);
			return slot;
		}
		return (skeleton, Part("a1", a), Part("b1", b), Part("a2", a), Part("b2", b));
	}

	private static string Names(IEnumerable<Slot> order) => string.Join(" ", order.Select(s => s.Name));

	// The order an animation draws the parts in at a frame
	private static string OrderAt(Skeleton skeleton, Animation animation, int frame)
	{
		skeleton.SetToSetupPose();
		animation.Apply(skeleton, frame / (float)Fps);
		return Names(skeleton.DrawOrder);
	}

	[Fact]
	public void Parts_change_places_whatever_bones_theyre_on()
	{
		var (skeleton, a1, b1, a2, b2) = Rig();
		var order = skeleton.Slots.ToList();

		Assert.Equal("b2 a1 b1 a2", Names(DrawOrderOps.Moved(order, new[] { b2 }, 0)));            // to the back
		Assert.Equal("b1 a2 b2 a1", Names(DrawOrderOps.ToEnd(order, new[] { a1 }, front: true)));  // to the front
		Assert.Equal("a2 a1 b1 b2", Names(DrawOrderOps.Stepped(order, new[] { a1, b1 }, +1)));     // together, keeping their order
		Assert.Equal("a1 b1 a2 b2", Names(DrawOrderOps.Stepped(order, new[] { b2 }, +1)));         // in front already
		Assert.Equal("b1 a1 a2 b2", Names(DrawOrderOps.Stepped(order, new[] { b1 }, -1)));

		Assert.True(DrawOrderOps.SetSetupOrder(skeleton, DrawOrderOps.Moved(order, new[] { b2 }, 0)));
		Assert.Equal("b2 a1 b1 a2", Names(skeleton.Slots));
		Assert.Equal("b2 a1 b1 a2", Names(skeleton.DrawOrder));
		Assert.False(DrawOrderOps.SetSetupOrder(skeleton, skeleton.Slots.ToList()));   // no change
		Assert.False(DrawOrderOps.SetSetupOrder(skeleton, new[] { a1, b1, a2 }));      // not the same parts
		Assert.False(DrawOrderOps.SetSetupOrder(skeleton, new[] { a1, b1, a2, a2 }));
	}

	[Fact]
	public void An_animation_keys_the_order_from_a_frame_on()
	{
		var (skeleton, a1, b1, a2, b2) = Rig();
		var setup = skeleton.Slots.ToList();
		var attack = new Animation { Name = "attack", Duration = 30f / Fps };

		// From frame 10 the back part goes to the front; from frame 20 the order turns round
		Assert.True(DrawOrderOps.Key(attack, 10f / Fps, DrawOrderOps.ToEnd(setup, new[] { a1 }, front: true), setup));
		Assert.True(DrawOrderOps.Key(attack, 20f / Fps, new[] { b2, b1, a2, a1 }, setup));
		Assert.False(DrawOrderOps.Key(attack, 25f / Fps, new[] { b2, b1, a2, a1 }, setup));   // it's that already
		var keys = attack.DrawOrder!.Track.Keys;
		Assert.Equal(2, keys.Count);
		Assert.Equal("a1 b1 a2 b2", OrderAt(skeleton, attack, 0));
		Assert.Equal("a1 b1 a2 b2", OrderAt(skeleton, attack, 9));
		Assert.Equal("b1 a2 b2 a1", OrderAt(skeleton, attack, 10));
		Assert.Equal("b1 a2 b2 a1", OrderAt(skeleton, attack, 19));
		Assert.Equal("b2 b1 a2 a1", OrderAt(skeleton, attack, 25));
		skeleton.SetToSetupPose();
		Assert.Equal("a1 b1 a2 b2", Names(skeleton.DrawOrder));   // the rest order, until an animation says otherwise

		// A part added since goes where the setup order has it: just in front of the part it's in front of there
		var c = new Slot("c", a1.Bone) { SetupAttachment = "c" };
		skeleton.Slots.Insert(2, c);   // between b1 and a2
		Assert.Equal("b1 c a2 b2 a1", OrderAt(skeleton, attack, 10));

		// Renamed and deleted parts: the keys follow
		Assert.True(PartOps.RenamePart(skeleton, new[] { attack }, a1, "hand"));
		Assert.Equal(new[] { "b1", "a2", "b2", "hand" }, keys[0].Value);
		Assert.Equal("b1 c a2 b2 hand", OrderAt(skeleton, attack, 10));
		PartOps.DeletePart(skeleton, new[] { attack }, b2);
		Assert.Equal(new[] { "b1", "a2", "hand" }, keys[0].Value);
		RigOps.DeleteBone(skeleton, new[] { attack }, b1.Bone);   // b1 goes with its bone
		Assert.Equal(new[] { "a2", "hand" }, keys[0].Value);
		Assert.Equal("a2 hand c", OrderAt(skeleton, attack, 10));   // c: in front of the hand, as in the setup order
	}

	[Fact]
	public void Draw_order_keys_are_saved_and_cut_with_their_frames()
	{
		var (skeleton, a1, b1, a2, b2) = Rig();
		var setup = skeleton.Slots.ToList();
		var attack = new Animation { Name = "attack", Duration = 30f / Fps };
		DrawOrderOps.Key(attack, 10f / Fps, new[] { b2, a1, b1, a2 }, setup);
		DrawOrderOps.Key(attack, 20f / Fps, new[] { a2, b2, a1, b1 }, setup);

		// Saved and loaded: every frame as it was
		string json = JsonSerializer.Serialize(ProjectMapper.ToDto(skeleton, new[] { attack }, Fps), ProjectMapper.Json);
		var (loadedSkeleton, loaded) = ProjectMapper.FromDto(JsonSerializer.Deserialize<ProjectDto>(json, ProjectMapper.Json)!);
		for (int f = 0; f < 30; f++)
			Assert.Equal(OrderAt(skeleton, attack, f), OrderAt(loadedSkeleton, loaded[0], f));

		// Frames 15-24 as an animation sequence of their own, and cut out of the attack
		var piece = AnimationOps.Extract(attack, 15, 24, Fps, "middle");
		for (int f = 0; f < 10; f++)
			Assert.Equal(OrderAt(skeleton, attack, 15 + f), OrderAt(skeleton, piece, f));
		var original = ProjectMapper.CloneAnimation(attack, "before");
		AnimationOps.Cut(attack, 15, 24, Fps);
		for (int f = 0; f < 15; f++)
			Assert.Equal(OrderAt(skeleton, original, f), OrderAt(skeleton, attack, f));
		for (int f = 15; f < 20; f++)
			Assert.Equal(OrderAt(skeleton, original, f + 10), OrderAt(skeleton, attack, f));
	}
}
