using System.Numerics;
using Animator.Core.Import;
using Animator.Core.Rigging;
using Animator.Core.Scenes;
using Animator.Viewer.Files;
using Animator.Viewer.Import;
using Animator.Viewer.Rendering;
using Animator.Viewer.Tools;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace Animator.Desktop.Tests;

// The window for a test: shown, settled, pictured; and a project to open
internal static class Windows
{
	internal static string ShotsFolder =>
		Environment.GetEnvironmentVariable("HQ_SHOTS") is { Length: > 0 } folder ? folder : Path.Combine(AppContext.BaseDirectory, "screenshots");

	internal static MainWindow Show()
	{
		var window = new MainWindow { Width = 1200, Height = 760 };
		window.Show();
		Settle(window);
		window.ResetView();
		Settle(window);
		return window;
	}

	// The layout done, the scene posed, everything drawn
	internal static void Settle(MainWindow window)
	{
		Dispatcher.UIThread.RunJobs();
		window.Tick(0);
		Dispatcher.UIThread.RunJobs();
	}

	// What the window shows, saved as a picture; how many different colours it has (a blank
	// window has a handful)
	internal static int Shot(MainWindow window, string name)
	{
		Settle(window);
		var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		Directory.CreateDirectory(ShotsFolder);
		frame!.Save(Path.Combine(ShotsFolder, name + ".png"));
		return Colours(frame);
	}

	internal static unsafe int Colours(WriteableBitmap bitmap)
	{
		using var pixels = bitmap.Lock();
		var seen = new HashSet<uint>();
		for (int y = 0; y < pixels.Size.Height; y += 4)
		{
			var row = (uint*)(pixels.Address + y * pixels.RowBytes);
			for (int x = 0; x < pixels.Size.Width; x += 4) seen.Add(row[x]);
		}
		return seen.Count;
	}

	// A project made from the Photoshop file the engine's tests read
	internal static string LayersProject()
	{
		string folder = Path.Combine(Path.GetTempPath(), "hq-desktop-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		string psd = Path.Combine(AppContext.BaseDirectory, "Files", "imagemagick-layers.psd");
		using var plan = PsdImportPlan.FromDocument(PsdReader.Read(psd), "layers");
		var store = new AssetStore();
		var scene = new Scene();
		PsdImporter.IntoScene(scene, store, plan, Vector2.Zero);
		string path = Path.Combine(folder, "layers" + ProjectFile.Extension);
		ProjectFile.Save(path, scene, store, null);
		return path;
	}
}
