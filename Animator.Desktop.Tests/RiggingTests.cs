using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// Building the rig: new parts, the list's Bones and Layers, dragging rows, the draw order and
// masks, a drawn object given bones, parts onto their nearest bones
public class RiggingTests
{
	private static void Finish(Task task)
	{
		var until = DateTime.UtcNow.AddSeconds(30);
		while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
		Assert.True(task.IsCompleted);
		task.GetAwaiter().GetResult();
	}

	private static List<object> Rows(MainWindow window) => window.RigList.Items.OfType<ListBoxItem>().Select(i => i.Tag!).ToList();

	private static ListBoxItem RowOf(MainWindow window, object tag) => window.RigList.Items.OfType<ListBoxItem>().First(i => i.Tag == tag);

	// The middle of a row, in the window
	private static Point Middle(MainWindow window, ListBoxItem row) =>
		row.TranslatePoint(new Point(30, row.Bounds.Height / 2), window) ?? throw new InvalidOperationException("row not shown");

	[AvaloniaFact]
	public void Parts_are_made_and_dragged_onto_bones_and_bones_onto_bones()
	{
		var window = Windows.Show();
		var rig = window.State.Skeleton;
		var root = rig.Roots[0];
		var arm = window.Session.AddBone(root, snap: false);
		var leg = window.Session.AddBone(root, snap: false);
		window.RefreshRigList();

		// A blank part, on the picked bone
		window.State.Selected = root;
		window.State.SelectedSlot = null;
		window.AnswerNewPart = dialog =>
		{
			dialog.NameBox.Text = "hat";
			dialog.WidthBox.Text = "0";
			dialog.TryOk();
			Assert.Contains("1 to 4096", dialog.ErrorText.Text);
			dialog.WidthBox.Text = "32";
			dialog.TryOk();
			return Task.FromResult(true);
		};
		Finish(window.NewBlankPart());
		var hat = rig.Slots.Single(s => s.Name == "hat");
		Assert.Same(root, hat.Bone);
		Assert.Same(hat, window.State.SelectedSlot);

		// One from a picture
		string folder = Path.Combine(Path.GetTempPath(), "hq-rig-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		string png = Path.Combine(folder, "boot.png");
		using (var bitmap = new SKBitmap(20, 10))
		{
			bitmap.Erase(SKColors.Brown);
			using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
			File.WriteAllBytes(png, data.ToArray());
		}
		window.State.Selected = root;
		window.State.SelectedSlot = null;
		window.AnswerPartPicture = () => Task.FromResult<string?>(png);
		Finish(window.NewPartFromPicture());
		var boot = rig.Slots.Single(s => s.Name == "boot");

		// Dropped: a part onto a bone (or a part's row: its bone), a bone onto another
		window.DropOnRow(hat, arm, upper: false);
		Assert.Same(arm, hat.Bone);
		window.DropOnRow(leg, arm, upper: false);
		Assert.Same(arm, leg.Parent);
		window.DropOnRow(arm, leg, upper: false);   // (not under its own child)
		Assert.Same(root, arm.Parent);
		window.State.Undo.Undo();
		Assert.Same(root, leg.Parent);

		// Dragged with the mouse
		window.RefreshRigList();
		Windows.Settle(window);
		var from = Middle(window, RowOf(window, boot));
		var to = Middle(window, RowOf(window, leg));
		window.MouseDown(from, MouseButton.Left);
		window.MouseMove(new Point(from.X, from.Y + 8));
		window.MouseMove(to);
		window.MouseUp(to, MouseButton.Left);
		Assert.Same(leg, boot.Bone);
		Windows.Shot(window, "rig-bones");
	}

	[AvaloniaFact]
	public void A_drawn_objects_layers_are_reordered_masked_and_given_bones()
	{
		var window = Windows.Show();
		Assert.True(window.TryOpen(Windows.LayersProject(), out string? problem), problem);
		if (window.Session.SceneLevel) window.EnterObject(window.Session.Scene.Objects[0]);
		Windows.Settle(window);
		var rig = window.State.Skeleton;
		Assert.True(window.ShowingLayers);
		Assert.True(rig.Slots.Count >= 3);
		Assert.Equal(rig.Slots.Reverse().Cast<object>(), Rows(window));   // front first

		// The one at the back dropped over the front one: in front of everything
		var back = rig.Slots[0];
		var front = rig.Slots[^1];
		window.DropOnRow(back, front, upper: true);
		Assert.Same(back, rig.Slots[^1]);
		Assert.Same(back, Rows(window)[0]);

		// The order keys: a step back, then all the way back
		window.State.SelectedSlot = back;
		window.State.Selected = back.Bone;
		Assert.True(window.HandleKey(Key.OemOpenBrackets, KeyModifiers.Control));
		Assert.Same(back, rig.Slots[^2]);
		Assert.True(window.HandleKey(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift));
		Assert.Same(back, rig.Slots[0]);

		// A mask for the parts above it
		var menu = window.OrderMenu(back);
		var mask = menu.Items.OfType<MenuItem>().Last();
		Assert.StartsWith("Clip the parts above it", mask.Header as string);
		mask.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		Assert.True(back.IsMask);
		Windows.Settle(window);
		Assert.Contains("◐ mask", ((TextBlock)RowOf(window, back).Content!).Inlines!.Text);
		Windows.Shot(window, "rig-layers");

		// Given bones: a rigged object, every part where it was
		var before = rig.Slots.Select(s => s.Name).ToList();
		window.GiveBones(suggest: true);
		Assert.Equal(ObjectKind.Rigged, window.Session.Active!.Kind);
		Assert.False(window.ShowingLayers);
		Assert.True(window.BonesView.IsVisible);
		Assert.Equal(before, window.State.Skeleton.Slots.Select(s => s.Name).ToList());
		window.PartsToNearestBones(window.State.Skeleton.Slots.ToList(), "every part");
		Assert.NotEqual("", window.RigHint.Text);
		Windows.Settle(window);
		Windows.Shot(window, "rig-given-bones");

		window.LayersView.IsChecked = true;
		window.LayersView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.True(window.ShowingLayers);
		Assert.Contains("on ", ((TextBlock)window.RigList.Items.OfType<ListBoxItem>().First().Content!).Inlines!.Text);
	}
}
