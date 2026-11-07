using Avalonia;

namespace Animator.Desktop;

// harlequin-editor [project.hqnap]: the editor, with that project open (else a new character)
public static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		App.StartupFile = args.FirstOrDefault(a => !a.StartsWith('-'));
		return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
		.UsePlatformDetect()
		.WithInterFont()
		.LogToTrace();
}
