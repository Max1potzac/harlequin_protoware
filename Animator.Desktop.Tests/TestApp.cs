using Animator.Desktop;
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Animator.Desktop.Tests.TestApp))]

namespace Animator.Desktop.Tests;

// The editor's application, on the headless platform, drawing with Skia (not the headless
// stand-in), so frames can be captured
public static class TestApp
{
	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
		.UseSkia()
		.UseHarfBuzz()
		.WithInterFont()
		.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
