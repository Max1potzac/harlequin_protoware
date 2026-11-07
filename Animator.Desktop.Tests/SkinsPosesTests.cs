using Animator.Core.Rigging;
using Animator.Viewer;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Animator.Desktop.Tests;

// The Skins and Poses tabs on the left
public class SkinsPosesTests
{
	[AvaloniaFact]
	public void Picked_parts_become_a_skin_which_is_named_and_hides_others()
	{
		var window = Windows.Show();
		Assert.True(window.TryOpen(Windows.LayersProject(), out _));
		var rig = window.State.Skeleton;
		var head = rig.Slots.First(s => s.Name == "head");
		var body = rig.Slots.First(s => s.Name == "body");
		window.State.Pick(new object[] { head }, head);

		window.MakeSkinFromParts(new[] { head });
		var skin = Assert.Single(rig.Skins);
		Assert.Same(skin, window.PickedSkin);
		Assert.True(rig.ShownSkins.Contains(skin));
		Assert.Equal(skin.Name, window.SkinName.Text);

		// Named, put in a category, made to hide the body
		window.SkinName.Text = "Big head";
		window.CommitSkinName();
		Assert.Equal("Big head", skin.Name);
		window.SkinCategory.Text = "Heads";
		window.CommitSkinKind();
		Assert.Equal("Heads", skin.Category);
		window.State.Pick(new object[] { body }, body);
		window.HidePickedInSkin(true);
		Assert.Contains("body", skin.Hides);

		window.LeftTabs.SelectedIndex = 1;
		Windows.Shot(window, "skins-tab");

		// Off, and on again; the hiding undone
		window.ShowSkin(skin, false);
		Assert.False(rig.ShownSkins.Contains(skin));
		window.ShowSkin(skin, true);
		window.State.Undo.Undo();
		window.State.Undo.Undo();
		Assert.DoesNotContain("body", skin.Hides);
	}

	[AvaloniaFact]
	public void A_pose_is_kept_renamed_and_put_on_again()
	{
		var window = Windows.Show();
		var root = window.State.Skeleton.Roots[0];
		float rest = root.SetupRotation;

		window.KeepPose();
		var pose = Assert.Single(window.State.Skeleton.Poses);
		Assert.Same(pose, window.PickedPose);
		window.PoseName.Text = "standing";
		window.CommitPoseName();
		Assert.Equal("standing", pose.Name);

		// Turned, then the pose put on again (Build: the rest pose)
		window.State.EditStructure(() =>
		{
			root.SetupRotation = rest + 1f;
			return "Turn";
		});
		window.ApplyPickedPose(mirrored: false);
		Windows.Settle(window);
		Assert.Equal(rest, root.SetupRotation, 3);

		// Copied and pasted (nothing to change: it says so in Animate)
		window.CopyThePose();
		window.PasteThePose(mirrored: false);
		Assert.Equal(rest, root.SetupRotation, 3);

		window.LeftTabs.SelectedIndex = 2;
		Windows.Shot(window, "poses-tab");
	}
}
