using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Animator.Desktop;

// The application: Fluent controls, dark like the Windows editor's default theme, and one
// editor window
public sealed class App : Application
{
	public static string? StartupFile { get; set; }   // a project to open at the start

	public override void Initialize()
	{
		Styles.Add(new FluentTheme());
		RequestedThemeVariant = ThemeVariant.Dark;
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			var window = new MainWindow();
			desktop.MainWindow = window;
			if (StartupFile != null) window.OpenAtStart(StartupFile);
		}
		base.OnFrameworkInitializationCompleted();
	}
}
