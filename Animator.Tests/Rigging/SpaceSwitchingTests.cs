namespace Animator.Tests;

// Detach / attach keys and the throw helper (milestone 8.5)
public class SpaceSwitchingTests
{
	private const int Fps = 30;

	// A spinning root with an arm on it, plus a free "hand" bone that slides along
	private static (Skeleton Skeleton, Animation Animation, Bone Arm, Bone Hand) Rig()
	{
		var skeleton = new Skeleton();
		var root = new Bone { Name = "root", Length = 100 };
		var arm = new Bone { Name = "arm", Length = 50, SetupPosition = new Vector2(100, 0) };
		root.AddChild(arm);
		var hand = new Bone { Name = "hand", Length = 20, SetupPosition = new Vector2(-200, 50) };
		skeleton.Roots.Add(root);
		skeleton.Roots.Add(hand);

		var animation = new Animation { Name = "swing", Duration = 1f };   // 30 frames
		var spin = new RotateTimeline("root");
		spin.Track.Add(0f, 0f);
		spin.Track.Add(1f, 2f);
		animation.Timelines.Add(spin);
		var slide = new TranslateTimeline("hand");
		slide.Track.Add(0f, Vector2.Zero);
		slide.Track.Add(1f, new Vector2(300, 0));
		animation.Timelines.Add(slide);
		return (skeleton, animation, arm, hand);
	}

	private static Matrix3x2 WorldAt(Skeleton skeleton, Animation animation, Bone bone, int frame)
	{
		IkMatching.Evaluate(skeleton, animation, frame / (float)Fps);
		return bone.World;
	}

	private static void AssertClose(Matrix3x2 expected, Matrix3x2 actual, string where)
	{
		float[] e = { expected.M11, expected.M12, expected.M21, expected.M22, expected.M31, expected.M32 };
		float[] a = { actual.M11, actual.M12, actual.M21, actual.M22, actual.M31, actual.M32 };
		for (int i = 0; i < e.Length; i++)
			Assert.True(MathF.Abs(e[i] - a[i]) < 1e-3f, $"{where}: expected {expected}, got {actual}");
	}

	[Fact]
	public void Detaching_changes_nothing_up_to_the_switch_then_the_bone_stays_put()
	{
		var (skeleton, animation, arm, _) = Rig();
		var before = Enumerable.Range(0, 31).Select(f => WorldAt(skeleton, animation, arm, f)).ToList();

		Assert.True(SpaceSwitching.Switch(skeleton, animation, arm, 10, Fps, SpaceTimeline.World));

		for (int f = 0; f <= 10; f++) AssertClose(before[f], WorldAt(skeleton, animation, arm, f), $"frame {f}");
		for (int f = 11; f <= 30; f++) AssertClose(before[10], WorldAt(skeleton, animation, arm, f), $"frame {f}");
	}

	[Fact]
	public void A_held_bone_is_carried_by_its_holder()
	{
		var (skeleton, animation, arm, hand) = Rig();
		var atSwitch = WorldAt(skeleton, animation, arm, 12);

		Assert.True(SpaceSwitching.Switch(skeleton, animation, arm, 12, Fps, "hand"));

		AssertClose(atSwitch, WorldAt(skeleton, animation, arm, 12), "the switch frame");
		var offset = WorldAt(skeleton, animation, arm, 12) * Inverse(hand.World);
		for (int f = 13; f <= 30; f++)
		{
			var world = WorldAt(skeleton, animation, arm, f);
			AssertClose(offset, world * Inverse(hand.World), $"frame {f}");
		}
	}

	[Fact]
	public void Detaching_earlier_replaces_a_later_detach()
	{
		var (skeleton, animation, arm, _) = Rig();
		Assert.True(SpaceSwitching.Switch(skeleton, animation, arm, 20, Fps, SpaceTimeline.World));
		var atTen = WorldAt(skeleton, animation, arm, 10);

		Assert.True(SpaceSwitching.Switch(skeleton, animation, arm, 10, Fps, SpaceTimeline.World));

		Assert.Single(animation.Find<SpaceTimeline>("arm")!.Track.Keys);
		for (int f = 10; f <= 30; f++) AssertClose(atTen, WorldAt(skeleton, animation, arm, f), $"frame {f}");
		Assert.False(SpaceSwitching.Switch(skeleton, animation, arm, 15, Fps, SpaceTimeline.World));   // already detached
	}

	[Fact]
	public void A_bone_cannot_be_held_by_its_own_child()
	{
		var (skeleton, animation, _, _) = Rig();
		var root = skeleton.FindBone("root")!;

		Assert.False(SpaceSwitching.Switch(skeleton, animation, root, 5, Fps, "arm"));
		Assert.Null(animation.Find<SpaceTimeline>("root"));
	}

	[Fact]
	public void A_thrown_bone_starts_where_it_was_and_never_falls_through_the_floor()
	{
		var (skeleton, animation, arm, _) = Rig();
		var before = Enumerable.Range(0, 31).Select(f => WorldAt(skeleton, animation, arm, f)).ToList();
		var settings = new ThrowSettings { GroundY = -80 };

		Assert.True(ThrowBake.Bake(skeleton, animation, arm, 6, Fps, settings));

		for (int f = 0; f <= 6; f++) AssertClose(before[f], WorldAt(skeleton, animation, arm, f), $"frame {f}");
		for (int f = 7; f <= 30; f++)
			Assert.True(WorldAt(skeleton, animation, arm, f).Translation.Y >= settings.GroundY - 1e-3f, $"frame {f} is below the floor");
	}

	private static Matrix3x2 Inverse(Matrix3x2 m) =>
		Matrix3x2.Invert(m, out var inverse) ? inverse : throw new InvalidOperationException("not invertible");
}
