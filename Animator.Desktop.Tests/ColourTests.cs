using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;

namespace Animator.Desktop.Tests;

// Colour: a part's tint, an object's colour layers (the Rig tab's Colour view), and colour layers
// inside a drawing (the paint editor)
public class ColourTests
{
	private static void Settle() => Dispatcher.UIThread.RunJobs();

	private static Slider SliderIn(Panel panel, string name) =>
		panel.Children.OfType<DockPanel>().SelectMany(d => d.Children.OfType<Slider>()).First(s => s.Tag as string == name);

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
	public void A_part_is_tinted_from_its_swatch()
	{
		var (window, part) = WithPart();
		window.AnswerTint = () => Task.FromResult<SKColor?>(new SKColor(255, 0, 0, 128));
		window.PartTint.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Settle();
		Assert.Equal(new Vector3(1, 0, 0), part.SetupColor);
		Assert.Equal(0.5f, part.SetupOpacity, 2);
		window.State.Undo.Undo();
		Assert.Equal(Vector3.One, part.SetupColor);
	}

	[AvaloniaFact]
	public void An_objects_colour_layers_are_added_set_and_leave_parts_out()
	{
		var (window, part) = WithPart();
		window.ColourView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.True(window.ShowingColour);
		Assert.False(window.RigList.IsVisible);
		Assert.Contains("No colour layers yet", window.ColourHint.Text);

		// Hue / saturation: its hue at rest in Build
		window.AddObjectColour(ColorAdjustKind.HueSaturation);
		var layer = window.PickedColourLayer!;
		Assert.Single(window.State.Skeleton.ColorLayers);
		SliderIn(window.ColourSettings, "Hue").Value = 90;
		Assert.Equal(90f, layer.Adjust.Setup.Hue, 1);
		window.ColourSettings.Children.OfType<CheckBox>().First().IsChecked = true;   // Colorize
		Assert.True(layer.Adjust.Colorize);

		// The part left out
		var leave = window.ColourLeaveOutList.Children.OfType<CheckBox>().Single(b => b.Content as string == "shirt");
		leave.IsChecked = true;
		Assert.Contains(part, layer.LeftOut);

		// Animate: a key at the playhead, the rest value as it was
		window.State.AnimateMode = true;
		window.State.SetFrame(10);
		window.Frame(0);
		SliderIn(window.ColourSettings, "Hue").Value = -45;
		Assert.Equal(90f, layer.Adjust.Setup.Hue, 1);
		Assert.Equal(-45f, layer.Adjust.Now.Hue, 1);
		window.State.AnimateMode = false;
		window.Frame(0);

		// A tint, its colour picked
		window.AddObjectColour(ColorAdjustKind.Tint);
		var tint = window.PickedColourLayer!;
		window.AnswerColour = (_, _) => Task.FromResult<Vector3?>(new Vector3(0, 1, 0));
		window.ColourSettings.Children.OfType<DockPanel>().SelectMany(d => d.Children.OfType<Button>()).First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Settle();
		Assert.Equal(new Vector3(0, 1, 0), tint.Adjust.Setup.TintColor);
		Assert.Equal(2, window.ColourList.Items.Count);
		Windows.Settle(window);
		Windows.Shot(window, "colour-layers");

		// Back to the bones
		window.BonesView.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.False(window.ShowingColour);
		Assert.True(window.RigList.IsVisible);
	}

	[AvaloniaFact]
	public void A_drawing_gets_a_colour_layer_in_the_paint_editor()
	{
		var (window, part) = WithPart();
		Assert.True(window.OpenPaintEditorOn(part));
		window.Session.Paint.AddColorLayer(ColorAdjustKind.ReplaceColor);
		window.Frame(0);
		Assert.NotEmpty(window.PaintColourSettings.Children);
		var tolerance = SliderIn(window.PaintColourSettings, "Tolerance");
		tolerance.Value = 40;
		Assert.Equal(0.4f, window.Session.Paint.Layer!.Adjust!.Setup.Tolerance, 2);
		Windows.Settle(window);
		Windows.Shot(window, "colour-paint");
	}
}
