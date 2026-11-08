using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using SkiaSharp;
using Xunit;

namespace Animator.Desktop.Tests;

// Hitboxes and points: the Rig tab's Boxes view (added, named, of a kind, a box or a circle, on
// or off at rest and keyed in Animate, their numbers typed), dragged in the viewport, and J
public class BoxesTests
{
	private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

	private static (MainWindow Window, Slot Part) WithPart()
	{
		var window = Windows.Show();
		var root = window.State.Skeleton.Roots[0];
		using var picture = new SKBitmap(40, 40);
		picture.Erase(SKColors.Orange);
		var part = window.Session.CreatePart(root, "shirt", picture, 40, 40);
		window.State.Selected = root;
		window.State.SelectedSlot = part;
		window.Frame(0);
		return (window, part);
	}

	[AvaloniaFact]
	public void A_hitbox_is_added_set_dragged_and_keyed()
	{
		var (window, part) = WithPart();
		var skeleton = window.State.Skeleton;
		Click(window.BoxesView);
		Assert.True(window.ShowingBoxes);
		Assert.False(window.RigList.IsVisible);
		Assert.Contains("+ Hitbox", window.BoxHint.Text);

		// Round the picked part, named after it
		window.AddHitbox(HitboxShape.Box, "hurt");
		var box = Assert.IsType<Hitbox>(window.PickedBox);
		Assert.Single(skeleton.Hitboxes);
		Assert.Equal("shirt hurt", box.Name);
		Assert.Equal(part.Bone, box.Bone);
		window.Frame(0);
		Assert.Single(window.BoxList.Items);
		Assert.True(window.BoxNumbers["Width"].IsVisible);
		Assert.False(window.BoxNumbers["Diameter"].IsVisible);
		Assert.False(window.BoxNumbers["Facing °"].IsVisible);

		// Its numbers typed
		window.BoxNumbers["X"].Type("12");
		Assert.Equal(12f, box.Offset.X, 2);
		window.BoxNumbers["Width"].Type("50");
		Assert.Equal(50f, box.Size.X, 2);

		// Dragged by its middle in the viewport: one undo step
		Windows.Settle(window);
		var middle = window.Camera.WorldToScreen(box.World.Translation);
		window.ViewportMove(middle);
		Assert.Same(box, window.HoveredBox);
		var before = box.Offset;
		window.ViewportDown(middle, PointerButton.Left, 1);
		window.ViewportMove(middle + new Vector2(10, 0));
		window.ViewportMove(middle + new Vector2(30, 10));
		window.ViewportUp(middle + new Vector2(30, 10));
		Assert.True(Vector2.Distance(before, box.Offset) > 5);
		Windows.Shot(window, "boxes-hitbox");
		window.State.Undo.Undo();
		Assert.Equal(before, box.Offset);

		// Named, of another kind, a circle
		window.BoxName.Text = "body";
		window.CommitBoxName();
		Assert.Equal("body", box.Name);
		window.BoxKind.Text = "hit";
		window.CommitBoxKind(window.BoxKind.Text);
		Assert.Equal("hit", box.Kind);
		window.BoxShape.SelectedIndex = 1;
		Assert.Equal(HitboxShape.Circle, box.Shape);
		window.Frame(0);
		Assert.True(window.BoxNumbers["Diameter"].IsVisible);
		Assert.False(window.BoxNumbers["Width"].IsVisible);

		// Off at rest, keyed on in Animate
		window.BoxActive.IsChecked = false;
		Assert.False(box.SetupActive);
		window.State.AnimateMode = true;
		window.State.SetFrame(10);
		window.Frame(0);
		Assert.True(window.BoxOnHere.IsVisible);
		Click(window.BoxOnHere);
		window.State.SetFrame(10);
		window.Frame(0);
		Assert.True(box.Active);
		Assert.Contains("is on at frame 10", window.BoxHint.Text);
		window.State.AnimateMode = false;
		window.Frame(0);

		// Back to the bones
		Click(window.BonesView);
		Assert.False(window.ShowingBoxes);
		Assert.True(window.RigList.IsVisible);
	}

	[AvaloniaFact]
	public void A_point_faces_a_way_and_J_shows_them_outside_the_view()
	{
		var (window, _) = WithPart();
		var skeleton = window.State.Skeleton;
		window.State.SelectedSlot = null;
		Click(window.BoxesView);
		window.AddHitbox(HitboxShape.Circle, "block");
		window.AddPoint();
		var point = Assert.IsType<RigPoint>(window.PickedBox);
		Assert.Single(skeleton.Points);
		window.Frame(0);
		Assert.Equal(2, window.BoxList.Items.Count);
		Assert.True(window.BoxNumbers["Facing °"].IsVisible);
		Assert.False(window.BoxNumbers["Width"].IsVisible);
		window.BoxNumbers["Facing °"].Type("90");
		Assert.Equal(MathF.PI / 2, point.Rotation, 3);
		Windows.Shot(window, "boxes-point");

		// Picked from the list, then deleted
		window.BoxList.SelectedIndex = 0;
		Assert.IsType<Hitbox>(window.PickedBox);
		Click(window.BoxDelete);
		Assert.Empty(skeleton.Hitboxes);
		window.State.Undo.Undo();
		window.Frame(0);
		Assert.Single(skeleton.Hitboxes);

		// J: shown without the view
		Click(window.BonesView);
		Assert.False(window.ShowingHitboxes);
		Assert.True(window.HandleKey(Key.J, KeyModifiers.None));
		Assert.True(window.ShowingHitboxes);
	}
}
