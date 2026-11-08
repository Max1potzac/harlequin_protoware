using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using SkiaSharp;
using Xunit;

namespace Animator.Desktop.Tests;

// The rest of the shell: the layout remembered, the View menu's toggles (pixel filtering,
// snapping, locking the guides, the grid's size), the Paint tool, and themes (the built-in high
// contrast one, and a theme file of one's own)
public class LayoutTests
{
	[AvaloniaFact]
	public void The_layout_is_put_back_as_it_was_left()
	{
		var window = Windows.Show();
		var s = window.Settings;
		string? tab = s.RigTab;
		try
		{
			s.LeftPanelWidth = 320;
			s.RightPanelWidth = 340;
			s.TimelineHeight = 240;
			s.RigTab = "boxes";
			window.RestoreLayout();
			window.RefreshRigList();
			Windows.Settle(window);
			Assert.Equal(320, window.LeftPanelWidth);
			Assert.Equal(340, window.RightPanelWidth);
			Assert.Equal(240, window.TimelineHeight);
			Assert.True(window.ShowingBoxes);

			// Remembered the other way: the view and the widths as they are now
			window.BonesView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			window.LeftTabs.SelectedIndex = 2;
			window.RememberLayout();
			Assert.Equal("poses", s.RigTab);
			Assert.Equal(320, s.LeftPanelWidth!.Value, 0);
			Assert.Equal(240, s.TimelineHeight!.Value, 0);
		}
		finally
		{
			window.LeftTabs.SelectedIndex = 0;
			s.RigTab = tab;
			s.LeftPanelWidth = s.RightPanelWidth = s.TimelineHeight = null;
			s.Save();
		}
	}

	[AvaloniaFact]
	public void The_view_menu_toggles_and_the_paint_tool()
	{
		var window = Windows.Show();
		var s = window.Settings;
		bool snapGrid = s.SnapToGrid, lockGuides = s.LockGuides;
		try
		{
			// Pixel filtering (P), with its check mark
			bool pixels = window.MenuItemFor("view.pixels").IsChecked;
			Assert.True(window.HandleKey(Key.P, KeyModifiers.None));
			window.SyncViewMenu();
			Assert.NotEqual(pixels, window.MenuItemFor("view.pixels").IsChecked);

			// Snapping and locking the guides, from the menu
			window.MenuItemFor("view.snapGrid").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
			Assert.NotEqual(snapGrid, s.SnapToGrid);
			window.MenuItemFor("view.lockGuides").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
			Assert.NotEqual(lockGuides, s.LockGuides);
			window.SyncViewMenu();
			Assert.Equal(s.LockGuides, window.MenuItemFor("view.lockGuides").IsChecked);

			// The grid's size (an undo step), and the grid shows
			window.SetGridSize(16);
			Assert.Equal(16f, window.Session.Scene.GridSize);
			Assert.True(s.ShowGrid);
			window.State.Undo.Undo();
			Assert.NotEqual(16f, window.Session.Scene.GridSize);
		}
		finally
		{
			s.SnapToGrid = snapGrid;
			s.LockGuides = lockGuides;
			s.Save();
		}

		// The Paint tool: one click on a part opens it in the paint editor
		var root = window.State.Skeleton.Roots[0];
		using var picture = new SKBitmap(40, 40);
		picture.Erase(SKColors.Orange);
		var part = window.Session.CreatePart(root, "shirt", picture, 40, 40);
		Assert.True(window.HandleKey(Key.D4, KeyModifiers.None));
		Windows.Settle(window);
		var drawing = window.State.Skeleton.FindAttachment(part.Name, part.SetupAttachment!)!;
		var middle = window.Camera.WorldToScreen(Vector2.Transform(drawing.Offset, part.Bone.World));
		window.ViewportDown(middle, PointerButton.Left, 1);
		window.ViewportUp(middle);
		Assert.True(window.Session.PaintOpen);
		window.ClosePaintEditor();
		Assert.True(window.HandleKey(Key.D1, KeyModifiers.None));
	}

	[AvaloniaFact]
	public void Themes_colour_the_window_and_theme_files_are_read()
	{
		var window = Windows.Show();
		try
		{
			// High contrast: the canvases' colours too
			Themes.Apply(Themes.HighContrast);
			Windows.Settle(window);
			Assert.Equal(SKColor.Parse("#101010"), CanvasTheme.Sk("Viewport"));
			Windows.Shot(window, "theme-high-contrast");

			// One's own theme file, built on Light; a broken one is left out, saying why
			Directory.CreateDirectory(Themes.UserFolder);
			File.WriteAllText(Path.Combine(Themes.UserFolder, "sunny.json"), "{ \"name\": \"Sunny\", \"base\": \"Light\", \"colors\": { \"Accent\": \"#FF8800\", \"Viewport\": \"#FFF4DD\" } }");
			File.WriteAllText(Path.Combine(Themes.UserFolder, "broken.json"), "{ \"name\": \"Broken\", \"colors\": { \"Nope\": \"#000\" } }");
			var problems = new List<string>();
			var all = Themes.All(problems);
			Assert.Contains(all, t => t.Name == "Sunny");
			Assert.DoesNotContain(all, t => t.Name == "Broken");
			Assert.Contains(problems, p => p.Contains("broken.json"));
			var sunny = Themes.Find("Sunny");
			Assert.True(Themes.IsLight(sunny));
			Themes.Apply(sunny);
			Windows.Settle(window);
			Assert.Equal(SKColor.Parse("#FFF4DD"), CanvasTheme.Sk("Viewport"));
			Assert.Equal(SKColor.Parse("#F4F5F7"), CanvasTheme.Sk("Canvas"));   // (from Light)
			Assert.True(File.Exists(Path.Combine(Themes.PrepareFolder(), "example.json")));
		}
		finally
		{
			Themes.Apply(Themes.Dark);
			Windows.Settle(window);
		}
	}
}
