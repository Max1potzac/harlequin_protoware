using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// The bone panel's constraints: look at, IK chains and their keys, what a bone follows (and the
// throw helper), follow and jiggle
public class ConstraintsTests
{
	private static void Finish(Task task)
	{
		var until = DateTime.UtcNow.AddSeconds(30);
		while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		Assert.True(task.IsCompleted);
		task.GetAwaiter().GetResult();
	}

	private static void Show(MainWindow window, Bone bone)
	{
		window.State.Selected = bone;
		window.State.SelectedSlot = null;
		window.Frame(0);
	}

	private static void Choose(ComboBox box, string label) =>
		box.SelectedItem = box.Items.Cast<object>().First(i => i.ToString() == label);

	// root → upper → lower (an arm), and a hand target on the root
	private static (MainWindow Window, Bone Root, Bone Upper, Bone Lower, Bone Target) Arm()
	{
		var window = Windows.Show();
		var root = window.State.Skeleton.Roots[0];
		var upper = window.Session.AddBone(root, snap: false);
		var lower = window.Session.AddBone(upper, snap: false);
		var target = window.Session.AddBone(root, snap: false);
		window.RefreshRigList();
		return (window, root, upper, lower, target);
	}

	[AvaloniaFact]
	public void An_IK_chain_is_made_and_its_keys_work()
	{
		var (window, _, upper, lower, target) = Arm();
		Show(window, lower);
		Choose(window.IkLength, "2 bones");
		var ik = window.Session.IkEndingAt(lower);
		Assert.NotNull(ik);
		Assert.Equal(new[] { upper, lower }, ik!.Bones);
		window.Frame(0);
		Assert.True(window.IkTarget.IsEnabled);
		Choose(window.IkTarget, target.Name);
		Assert.Same(target, ik.Target);

		// The elbow flipped from the box, and back with B
		window.IkFlip.IsChecked = true;
		Assert.True(ik.BendDirection < 0);
		Assert.True(window.HandleKey(Key.B, KeyModifiers.None));
		Assert.True(ik.BendDirection > 0);
		Windows.Settle(window);
		Windows.Shot(window, "bone-ik");

		// The middle bone can't start a chain of its own
		Show(window, upper);
		Assert.False(window.IkLength.IsEnabled);
		Assert.Contains("Part of IK chain", window.BoneHint.Text);
	}

	[AvaloniaFact]
	public void Bones_look_follow_jiggle_and_are_detached_and_thrown()
	{
		var (window, root, upper, lower, target) = Arm();

		// Look at: the target, with its numbers
		Show(window, lower);
		Choose(window.LookTarget, target.Name);
		Assert.Same(target, window.Session.LookOf(lower)!.Target);
		window.Frame(0);
		Assert.True(window.LookShapeBox.IsEnabled);
		Assert.NotNull(window.Session.BoneValue(lower, BoneField.Follow));

		// Follow another bone, half its turn; and jiggle
		Show(window, target);
		Choose(window.CopySource, upper.Name);
		var copy = window.Session.CopyOf(target);
		Assert.Same(upper, copy!.Source);
		window.Frame(0);
		window.ConstraintSliders[ConstraintValue.CopyRotate].Value = 0.5;
		Assert.Equal(0.5f, copy.RotateMix, 3);
		window.JiggleOn.IsChecked = true;
		Assert.NotNull(window.Session.JiggleOf(target));
		window.Frame(0);
		Assert.Contains("follows", window.ConstraintHint.Text);
		Assert.Contains("jiggles", window.ConstraintHint.Text);

		// Animate: detached at frame 10, then thrown from frame 20
		window.State.AnimateMode = true;
		window.State.SetFrame(10);
		Show(window, lower);
		Assert.True(window.SpaceBox.IsEnabled);
		Choose(window.SpaceBox, "nothing (detached)");
		Assert.Equal(SpaceTimeline.World, SpaceSwitching.SpaceAt(window.State.Animation, lower, window.State.Time));
		window.State.SetFrame(5);
		Assert.Null(SpaceSwitching.SpaceAt(window.State.Animation, lower, window.State.Time));

		window.State.SetFrame(20);
		window.Frame(0);
		window.AnswerThrow = dialog =>
		{
			dialog.Gravity.Text = "heavy";
			dialog.TryOk();
			Assert.Contains("isn't a number", dialog.ErrorText.Text);
			dialog.Gravity.Text = "1200";
			dialog.TryOk();
			return Task.FromResult(dialog.ErrorText.Text == "");
		};
		int steps = window.State.Undo.UndoCount;
		Finish(window.ThrowShown());
		Assert.True(window.State.Undo.UndoCount > steps);
		Windows.Settle(window);
		Windows.Shot(window, "bone-follows");
	}
}
