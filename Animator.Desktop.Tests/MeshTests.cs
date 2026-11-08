using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace Animator.Desktop.Tests;

// The part panel's Mesh box: a mesh made over a picture, weighted, its points dragged in the
// viewport, removed
public class MeshTests
{
	[AvaloniaFact]
	public void A_mesh_is_made_weighted_and_its_points_dragged()
	{
		var window = Windows.Show();
		var state = window.State;
		var root = state.Skeleton.Roots[0];
		var arm = window.Session.AddBone(root, snap: false);
		using var picture = new SKBitmap(80, 80);
		picture.Erase(SKColors.Teal);
		var cloth = window.Session.CreatePart(root, "cloth", picture, 80, 80);
		state.Selected = root;
		state.SelectedSlot = cloth;
		window.Frame(0);
		Assert.True(window.MeshBoxShown);
		Assert.Contains("No mesh", window.MeshHint.Text);

		// Made: its points editable at once
		window.MakeMesh(6);
		var mesh = window.Session.MeshOf(cloth)!.Value.Mesh!;
		Assert.True(mesh.Points.Length >= 49);
		Assert.Equal(MeshMode.Points, window.MeshTool);
		Assert.True(window.MeshPoints.IsChecked);
		Assert.False(window.MeshWeights.IsEnabled);   // (no weights yet)

		// Weighted from the nearest bones: painting them is possible now
		var menu = window.MeshWeightsMenu()!;
		menu.Items.OfType<MenuItem>().First(i => i.Header as string == "From the nearest bones").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		mesh = window.Session.MeshOf(cloth)!.Value.Mesh!;
		Assert.True(mesh.Weighted);
		window.Frame(0);
		Assert.True(window.MeshWeights.IsEnabled);
		Assert.Contains("bending it", window.MeshHint.Text);

		// A point found under the mouse and dragged (Build: where it sits on the picture)
		Windows.Settle(window);
		var size = window.Viewport.SizeInUnits;
		Vector2? over = null;
		for (float y = 0; y < size.Y && over == null; y += 2)
			for (float x = 0; x < size.X && over == null; x += 2)
			{
				window.ViewportMove(new Vector2(x, y));
				if (window.MeshHover >= 0) over = new Vector2(x, y);
			}
		Assert.NotNull(over);
		int point = window.MeshHover;
		var before = mesh.Points[point];
		window.ViewportDown(over!.Value, PointerButton.Left, 1);
		window.ViewportMove(over.Value + new Vector2(5, 0));
		window.ViewportMove(over.Value + new Vector2(20, 10));
		window.ViewportUp(over.Value + new Vector2(20, 10));
		var moved = window.Session.MeshOf(cloth)!.Value.Mesh!.Points[point];
		Assert.True(Vector2.Distance(before, moved) > 5);
		Windows.Shot(window, "mesh-box");
		state.Undo.Undo();
		Assert.Equal(before, window.Session.MeshOf(cloth)!.Value.Mesh!.Points[point]);

		// In Animate: no making or removing (dragging points keys deforms there)
		state.AnimateMode = true;
		window.Frame(0);
		Assert.False(window.MeshWeights.IsEnabled);
		state.AnimateMode = false;
		window.Frame(0);

		// Removed
		window.MeshTool = MeshMode.None;
		Assert.True(window.Session.RemoveMesh(cloth));
		window.Frame(0);
		Assert.Null(window.Session.MeshOf(cloth)!.Value.Mesh);
		Assert.Contains("No mesh", window.MeshHint.Text);
	}
}
