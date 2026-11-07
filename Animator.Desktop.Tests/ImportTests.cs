using Animator.Core.Scenes;
using Animator.Desktop.Import;
using Animator.Viewer;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
}
