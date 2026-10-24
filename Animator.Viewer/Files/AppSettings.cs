using System.IO;
using System.Text.Json;

namespace Animator.Viewer.Files;

// Things the program remembers between runs (not part of any project), in
// %AppData%\Harlequin\settings.json. A missing or broken file just means the defaults.
public sealed class AppSettings
{
	public string? FfmpegPath { get; set; }      // ffmpeg.exe, for exporting videos
	public string? ExportFolder { get; set; }    // where the last export went
	public string? ImportFolder { get; set; }    // where the last imported sprite sheet came from

	private static string FilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Harlequin", "settings.json");

	public static AppSettings Load()
	{
		try
		{
			if (File.Exists(FilePath))
				return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
		}
		catch (Exception)
		{
			// Unreadable: start from the defaults
		}
		return new AppSettings();
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
		}
		catch (Exception)
		{
			// Not being able to remember a setting isn't worth bothering anyone about
		}
	}
}
