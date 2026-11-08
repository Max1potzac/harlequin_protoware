using Animator.Core.Rigging;
using Animator.Viewer.Tools;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Animator.Desktop.Tests;

// Picking several bones and parts in the rig list, hiding and locking them, selection sets and
// bone colours
public class PickingTests
{
	private static void Finish(Task task)
	{
		var until = DateTime.UtcNow.AddSeconds(30);
		while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		Assert.True(task.IsCompleted);
		task.GetAwaiter().GetResult();
	}

	private static ListBoxItem RowOf(MainWindow window, object tag) => window.RigList.Items.OfType<ListBoxItem>().First(i => i.Tag == tag);

	// A point on a row (its label, or its eye), in the window
	private static Point On(MainWindow window, ListBoxItem row, Func<StackPanel, Control>? part = null)
	{
		Control target = part != null ? part((StackPanel)row.Content!) : MainWindow.RowLabel(row);
		return target.TranslatePoint(new Point(4, target.Bounds.Height / 2), window) ?? throw new InvalidOperationException("not shown");
	}

	private static void Click(MainWindow window, Point at, RawInputModifiers mods = RawInputModifiers.None)
	{
		window.MouseDown(at, MouseButton.Left, mods);
		window.MouseUp(at, MouseButton.Left, mods);
		Dispatcher.UIThread.RunJobs();
	}

	// root, with an arm and a leg on it, and a part on each
	private static (MainWindow Window, Bone Root, Bone Arm, Bone Leg) Rigged()
	{
		var window = Windows.Show();
		var root = window.State.Skeleton.Roots[0];
		var arm = window.Session.AddBone(root, snap: false);
		var leg = window.Session.AddBone(root, snap: false);
		window.Session.CreatePart(arm, "sleeve", null, 10, 10);
		window.Session.CreatePart(leg, "boot", null, 10, 10);
		window.RefreshRigList();
		Windows.Settle(window);
		return (window, root, arm, leg);
	}

	[AvaloniaFact]
	public void Rows_are_picked_with_Ctrl_and_Shift_and_hidden_and_locked()
	{
		var (window, root, arm, leg) = Rigged();
		var state = window.State;

		Click(window, On(window, RowOf(window, root)));
		Assert.Equal(new object[] { root }, state.Picked);
		Click(window, On(window, RowOf(window, leg)), RawInputModifiers.Control);
		Assert.Equal(2, state.Picked.Count);
		Assert.Contains(leg, state.Picked);
		Click(window, On(window, RowOf(window, root)), RawInputModifiers.Control);   // (picked or not)
		Assert.Equal(new object[] { leg }, state.Picked);

		// Shift: every row from the selected one (leg) up to the root
		Click(window, On(window, RowOf(window, root)), RawInputModifiers.Shift);
		Assert.Equal(5, state.Picked.Count);   // root, arm, sleeve, leg, boot... from leg up: root..leg
		Assert.Same(leg, state.Primary);
		Assert.NotNull(RowOf(window, arm).Background);   // (a band on the picked rows)

		// The eye: hidden while editing, and its part with it
		Click(window, On(window, RowOf(window, arm), row => row.Children[0]));
		Assert.True(arm.Hidden);
		var sleeve = state.Skeleton.Slots.Single(s => s.Name == "sleeve");
		Assert.True(sleeve.IsHidden);
		Assert.Equal("○", ((TextBlock)((StackPanel)RowOf(window, arm).Content!).Children[0]).Text);
		Assert.DoesNotContain(arm, state.Picked);   // (what's hidden can't stay picked)
		Assert.True(window.HandleKey(Key.H, KeyModifiers.Alt));   // show everything
		Assert.False(arm.Hidden);

		// The keys: H hides the picked, Shift+H the others; L locks, Ctrl+A picks what's shown and unlocked
		state.Pick(new object[] { leg });
		Assert.True(window.HandleKey(Key.H, KeyModifiers.Shift));
		Assert.True(arm.Hidden);
		Assert.False(leg.Hidden);
		window.HandleKey(Key.H, KeyModifiers.Alt);
		state.Pick(new object[] { arm });
		Assert.True(window.HandleKey(Key.L, KeyModifiers.None));
		Assert.True(arm.Locked);
		Assert.True(window.HandleKey(Key.A, KeyModifiers.Control));
		Assert.DoesNotContain(arm, state.Picked);
		Assert.Contains(leg, state.Picked);
		Windows.Settle(window);
		Windows.Shot(window, "rig-picking");
		Click(window, On(window, RowOf(window, arm), row => row.Children[1]));   // (the lock: unlocked)
		Assert.False(arm.Locked);
		window.State.Undo.Undo();
		Assert.True(arm.Locked);
	}

	[AvaloniaFact]
	public void Sets_pick_their_members_and_bones_get_colours()
	{
		var (window, root, arm, leg) = Rigged();
		var state = window.State;
		state.Pick(new object[] { arm, leg });

		window.AnswerSetName = suggested =>
		{
			Assert.Equal("set", suggested);
			return Task.FromResult<string?>("limbs");
		};
		Finish(window.NewSetFromPicked());
		var set = Assert.Single(state.Skeleton.Sets);
		Assert.Equal("limbs", set.Name);
		Windows.Settle(window);
		Assert.Contains(window.SetChips.Children.OfType<Button>(), b => b.Content as string == "limbs");

		state.Pick(new object[] { root });
		window.PickSet(set, KeyMods.None);
		Assert.Equal(2, state.Picked.Count);
		window.PickSet(set, KeyMods.Control);   // (all picked: let go)
		Assert.Empty(state.Picked);
		state.Pick(new object[] { root });
		window.PickSet(set, KeyMods.Shift);     // (picked too)
		Assert.Equal(3, state.Picked.Count);

		// Changed from its menu: just the root in it
		state.Pick(new object[] { root });
		var menu = window.SetMenu(set);
		menu.Items.OfType<MenuItem>().First(i => (i.Header as string)!.StartsWith("Just the picked")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		Assert.Equal(new[] { root }, set.Bones);
		Assert.Empty(set.Slots);
		state.Undo.Undo();
		Assert.Equal(2, set.Bones.Count);

		// Blue bones, and their rows say so
		state.Pick(new object[] { arm, leg });
		window.BoneColourMenu().Items.OfType<MenuItem>().First(i => i.Header as string == "Blue").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		Assert.NotNull(arm.EditorColor);
		Assert.Equal(arm.EditorColor, leg.EditorColor);
		Windows.Settle(window);
		Assert.Contains(((StackPanel)RowOf(window, arm).Content!).Children.OfType<TextBlock>(), t => t.Text == "■ ");
		Windows.Shot(window, "rig-sets");
	}
}
