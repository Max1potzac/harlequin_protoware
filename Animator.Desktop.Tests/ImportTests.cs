using Animator.Core.Scenes;
using Animator.Desktop.Import;
using Animator.Viewer;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;

namespace Animator.Desktop.Tests;

// File > Import: the Photoshop dialog, and what comes in
public class ImportTests
{
	private static string Psd => Path.Combine(AppContext.BaseDirectory, "Files", "imagemagick-layers.psd");

	[AvaloniaFact]
	public void The_Photoshop_dialog_shows_the_layers_and_a_suggested_rig()
	{
		using var plan = EditorSession.ReadPhotoshop(Psd, out string? why)!;
		Assert.NotNull(plan);
		var dialog = new PsdImportWindow(plan) { Width = 1100, Height = 720 };
		dialog.Show();
		Dispatcher.UIThread.RunJobs();
		Assert.Contains("drawn object", dialog.Summary.Text);

		dialog.RigBox.IsChecked = true;
		Assert.Contains("rigged object", dialog.Summary.Text);
		dialog.PickLayer(plan.Parts.Count - 1);
		var frame = dialog.CaptureRenderedFrame();
		Assert.NotNull(frame);
		Directory.CreateDirectory(Windows.ShotsFolder);
		frame!.Save(Path.Combine(Windows.ShotsFolder, "psd-import.png"));

		// Nothing ticked: it won't import
		foreach (var part in plan.Parts) part.Include = false;
		dialog.RigBox.IsChecked = false;
		dialog.TryImport();
		Assert.True(dialog.IsVisible);
		dialog.Close();
	}

	[AvaloniaFact]
	public void A_Photoshop_file_comes_in_as_a_rigged_object_and_opens()
	{
		var window = Windows.Show();
		int before = window.Session.Scene.Objects.Count;
		window.AnswerPsdImport = dialog =>
		{
			dialog.RigBox.IsChecked = true;
			return Task.FromResult(true);
		};
		var import = window.ImportPath(Psd);
		Dispatcher.UIThread.RunJobs();
		Assert.True(import.IsCompleted);
		Assert.Equal(before + 1, window.Session.Scene.Objects.Count);
		var made = window.Session.Active!;
		Assert.Equal(ObjectKind.Rigged, made.Kind);
		Assert.NotEmpty(made.Skeleton.Slots);
		window.State.Undo.Undo();
		Windows.Settle(window);
		Assert.Equal(before, window.Session.Scene.Objects.Count);
	}

	// A ball tweening across the stage over 20 frames, labels "bounce" and "roll" splitting them
	private static string BouncingFla()
	{
		var project = new XflTestProject { Width = 400, Height = 300, FrameRate = 30 };
		project.Picture("media/ball", 20, 20, SKColors.Red, round: true);
		project.Picture("media/box", 30, 30, SKColors.Blue);
		project.Symbol("ball", XflTestProject.Layer("1", XflTestProject.Frame(0, 1, XflTestProject.Bitmap("media/ball", Matrix3x2.CreateTranslation(-10, -10)))));
		project.Symbol("box", XflTestProject.Layer("1", XflTestProject.Frame(0, 1, XflTestProject.Bitmap("media/box", Matrix3x2.CreateTranslation(-15, -15)))));
		project.Scene("Scene 1",
			XflTestProject.Layer("labels",
				XflTestProject.Frame(0, 10, "", "name=\"bounce\" labelType=\"name\""),
				XflTestProject.Frame(10, 10, "", "name=\"roll\" labelType=\"name\"")) +
			XflTestProject.Layer("ball",
				XflTestProject.Frame(0, 10, XflTestProject.Instance("ball", Matrix3x2.CreateTranslation(100, 100), Vector2.Zero), "tweenType=\"motion\""),
				XflTestProject.Frame(10, 10, XflTestProject.Instance("ball", Matrix3x2.CreateTranslation(300, 120), Vector2.Zero))) +
			XflTestProject.Layer("box", XflTestProject.Frame(0, 20, XflTestProject.Instance("box", Matrix3x2.CreateTranslation(200, 200), Vector2.Zero))));
		string folder = Path.Combine(Path.GetTempPath(), "hq-desktop-xfl-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		return project.SaveFla(Path.Combine(folder, "bouncing.fla"));
	}

	[AvaloniaFact]
	public void An_Animate_project_comes_in_through_its_dialog()
	{
		var window = Windows.Show();
		int before = window.Session.Scene.Objects.Count;
		window.AnswerAnimateImport = dialog =>
		{
			Assert.Contains("drawn object", dialog.Summary.Text);
			dialog.SplitBox.IsChecked = true;   // an animation for each label
			Assert.Contains("2 animations", dialog.Summary.Text);
			dialog.StepBy(+5);
			Assert.Equal(5, dialog.Frame);
			dialog.PickPart(0);
			dialog.Show();
			Dispatcher.UIThread.RunJobs();
			var frame = dialog.CaptureRenderedFrame();
			Directory.CreateDirectory(Windows.ShotsFolder);
			frame?.Save(Path.Combine(Windows.ShotsFolder, "animate-import.png"));
			dialog.Close();
			return Task.FromResult(true);
		};
		var import = window.ImportPath(BouncingFla());
		Dispatcher.UIThread.RunJobs();
		Assert.True(import.IsCompleted);
		Assert.Equal(before + 1, window.Session.Scene.Objects.Count);
		Assert.Equal(2, window.Session.Active!.Animations.Count);
	}

	// A strip of four 16 × 16 frames, each a different colour, on a see-through sheet
	private static string Strip()
	{
		string folder = Path.Combine(Path.GetTempPath(), "hq-desktop-sheet-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		using var bitmap = new SKBitmap(64, 16, SKColorType.Rgba8888, SKAlphaType.Premul);
		bitmap.Erase(SKColors.Transparent);
		using (var canvas = new SKCanvas(bitmap))
			for (int i = 0; i < 4; i++)
				using (var paint = new SKPaint { Color = SKColor.FromHsl(i * 90, 80, 50) })
					canvas.DrawRect(new SKRect(i * 16 + 2, 2, i * 16 + 14, 14), paint);
		string path = Path.Combine(folder, "strip.png");
		using (var image = SKImage.FromBitmap(bitmap))
		using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
			File.WriteAllBytes(path, png.ToArray());
		return path;
	}

	[AvaloniaFact]
	public void A_sprite_sheet_is_cut_made_an_animation_and_brought_in()
	{
		var window = Windows.Show();
		int before = window.Session.Scene.Objects.Count;
		window.AnswerSheetImport = dialog =>
		{
			Assert.True(dialog.FindMode.IsChecked);   // see-through: its separate sprites
			Assert.Equal(4, dialog.Plan.Pieces.Count);

			// As a grid of 16 × 16 instead, then all four an animation
			dialog.GridMode.IsChecked = true;
			dialog.CellWidth.Text = dialog.CellHeight.Text = "16";
			dialog.Cut();
			Assert.Equal(4, dialog.Plan.Pieces.Count);
			foreach (var piece in dialog.Plan.Pieces) dialog.Picked.Add(piece);
			dialog.MakeAnimationOfPicked();
			Assert.Single(dialog.Plan.Animations);
			Assert.Equal(4, dialog.CurrentAnimation!.Frames.Count);
			Assert.Contains("one object", dialog.Summary.Text);
			dialog.Show();
			Dispatcher.UIThread.RunJobs();
			var frame = dialog.CaptureRenderedFrame();
			Directory.CreateDirectory(Windows.ShotsFolder);
			frame?.Save(Path.Combine(Windows.ShotsFolder, "sheet-import.png"));
			dialog.Close();
			return Task.FromResult(true);
		};
		var import = window.ImportPath(Strip());
		Dispatcher.UIThread.RunJobs();
		Assert.True(import.IsCompleted);
		Assert.True(window.Session.Scene.Objects.Count > before);
		Assert.True(window.Session.SceneLevel);   // (brought in as objects: at scene level, the first picked)
	}
}
