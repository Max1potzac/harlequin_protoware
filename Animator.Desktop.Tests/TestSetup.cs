using System.Runtime.CompilerServices;

namespace Animator.Desktop.Tests;

// The tests' settings and recovery copies are their own (never anyone's real ones)
internal static class TestSetup
{
	[ModuleInitializer]
	internal static void Isolate()
	{
		string folder = Path.Combine(Path.GetTempPath(), "hq-desktop-tests-" + Guid.NewGuid().ToString("N"));
		AppSettings.FilePath = Path.Combine(folder, "settings.json");
		Recovery.Folder = Path.Combine(folder, "Recovery");
	}
}
