using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Animator.Desktop.Tests;

// The paint editor beside the viewport
public class PaintTests
{
	[AvaloniaFact]
	public void A_parts_drawing_is_painted_and_the_editor_closed()
	{
		var window = Windows.Show();
		Assert.True(window.TryOpen(Windows.LayersProject(), out _));
		var body = window.State.Skeleton.Slots.First(s => s.Name == "body");
		window.State.SelectedSlot = body;
		window.State.Selected = body.Bone;
		Assert.True(window.OpenPaintEditorOn(body));
		Assert.True(window.PaintPanel.IsVisible);
		Assert.NotNull(window.Session.Paint.Asset);
		Windows.Shot(window, "paint-editor");   // (drawn once: the canvas fits the drawing)

		// A stroke across the middle of the canvas, with the headless mouse: one undo step
		var view = window.PaintView;
		var middle = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
		Assert.False(window.State.Undo.IsDirty);
		window.MouseMove(middle, RawInputModifiers.None);
		window.MouseDown(middle, MouseButton.Left, RawInputModifiers.None);
		window.MouseMove(middle + new Point(30, 10), RawInputModifiers.LeftMouseButton);
		window.MouseMove(middle + new Point(60, 20), RawInputModifiers.LeftMouseButton);
		window.MouseUp(middle + new Point(60, 20), MouseButton.Left, RawInputModifiers.None);
		Assert.True(window.State.Undo.IsDirty);

		// Its keys, with the mouse over it: E for the eraser, [ for a smaller brush
		Assert.True(window.HandleKey(Key.E, KeyModifiers.None));
		Assert.Same(window.Session.PaintTools.Eraser, window.Session.Paint.Tool);
		int size = window.Session.Paint.Size;
		window.HandleKey(Key.OemOpenBrackets, KeyModifiers.None);
		Assert.Equal(size - 1, window.Session.Paint.Size);
		Windows.Shot(window, "paint-stroke");

		window.ClosePaintEditor();
		Assert.False(window.PaintPanel.IsVisible);
		Assert.False(window.Session.PaintOpen);
	}

	[AvaloniaFact]
	public void Leaving_the_object_closes_the_paint_editor()
	{
		var window = Windows.Show();
		Assert.True(window.TryOpen(Windows.LayersProject(), out _));
		Assert.True(window.OpenPaintEditorOn(window.State.Skeleton.Slots[0]));
		window.LeaveObject();
		Windows.Settle(window);
		Assert.False(window.PaintPanel.IsVisible);
	}
}
