using System.Runtime.CompilerServices;
using Animator.Viewer.Files;

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
		MainWindow.MakeAudio = () => new ListeningAudio();   // (sounds are listened to, not played)
	}
}
