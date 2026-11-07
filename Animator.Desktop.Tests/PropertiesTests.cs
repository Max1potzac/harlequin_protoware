using Animator.Core.Keyframing;
using Animator.Viewer;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// The panel on the right: a bone's, a part's and an object's numbers, typed and dragged
public class PropertiesTests
{
	[AvaloniaFact]
	public void A_bones_numbers_are_typed_in_Build_and_keyed_in_Animate()
	{
		var window = Windows.Show();
		var bone = window.State.Skeleton.Roots[0];
		window.State.Selected = bone;
		Windows.Settle(window);
		Assert.Equal("root", window.BoneName.Text);
		Assert.Equal("60", window.BoneNumbers[BoneField.Length].Box.Text);

		// Build: the rest pose, one undo step
		window.BoneNumbers[BoneField.Length].Type("80");
		Assert.Equal(80f, bone.Length);
		window.BoneNumbers[BoneField.Length].Type("eighty");   // not a number: nothing changes
		Assert.Equal(80f, bone.Length);
		window.State.Undo.Undo();
		Assert.Equal(60f, bone.Length);

		// A new name
		window.BoneName.Text = "hips";
		window.RenameShown();
		Assert.Equal("hips", bone.Name);

		// Animate: a rotation typed is a key at the playhead
		window.HandleKey(Key.K, KeyModifiers.None);
		window.State.SetFrame(10);
		Windows.Settle(window);
		window.BoneNumbers[BoneField.Rotation].Type("45");
		Assert.NotNull(window.State.Animation.Find<RotateTimeline>("hips"));
		Assert.Equal(90f, EditorSession.Deg(bone.SetupRotation), 3);   // the rest pose as it was
		Windows.Shot(window, "bone-properties");
	}

	[AvaloniaFact]
	public void Dragging_a_numbers_label_is_one_undo_step()
	{
		var window = Windows.Show();
		var bone = window.State.Skeleton.Roots[0];
		window.State.Selected = bone;
		Windows.Settle(window);

		var label = window.BoneNumbers[BoneField.X].Label;
		var at = label.TranslatePoint(new Point(10, label.Bounds.Height / 2), window)!.Value;
		window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
		window.MouseMove(at + new Point(10, 0), RawInputModifiers.LeftMouseButton);
		window.MouseMove(at + new Point(25, 0), RawInputModifiers.LeftMouseButton);
		window.MouseUp(at + new Point(25, 0), MouseButton.Left, RawInputModifiers.None);
		Assert.Equal(25f, bone.SetupPosition.X, 3);
		window.State.Undo.Undo();
		Assert.Equal(0f, bone.SetupPosition.X, 3);
	}

	[AvaloniaFact]
	public void A_parts_placement_and_an_objects_place_are_typed()
	{
		var window = Windows.Show();
		Assert.True(window.TryOpen(Windows.LayersProject(), out _));
		var slot = window.State.Skeleton.Slots[0];
		window.State.SelectedSlot = slot;
		window.State.Selected = slot.Bone;
		Windows.Settle(window);
		Assert.Equal(slot.Name, window.PartName.Text);

		float x = window.Session.PartValue(slot, PartField.X)!.Value;
		window.PartNumbers[PartField.X].Type(EditorSession.Num(x + 12));
		Assert.Equal(x + 12, window.Session.PartValue(slot, PartField.X)!.Value, 3);
		window.PartOpacity.Type("50");
		Assert.Equal(0.5f, slot.SetupOpacity, 3);
		Windows.Shot(window, "part-properties");

		// At scene level, the object's place
		var obj = window.Session.Active!;
		window.LeaveObject();
		window.Session.SceneState.Selected = obj;
		Windows.Settle(window);
		window.ObjectNumbers["X"].Type("40");
		Assert.Equal(40f, obj.Position.X, 3);
		Windows.Shot(window, "object-properties");
	}
}
