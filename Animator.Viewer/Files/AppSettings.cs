using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Animator.Viewer.Files;

// Things the program remembers between runs (not part of any project), in
// %AppData%\Harlequin\settings.json. A missing or broken file just means the defaults.
public sealed class AppSettings
{
	public string? FfmpegPath { get; set; }      // ffmpeg.exe, for exporting videos
	public string? ExportFolder { get; set; }    // where the last export went
	public string? ImportFolder { get; set; }    // where the last imported file came from
	public bool KeepPartsInPlace { get; set; } = true;   // "Parts stay put when bones move" (rig panel)

	// Settings: the shortcuts changed from the defaults (command → its keys, see KeyMap), and which
	// mouse button picks, which moves things and which moves the view
	public Dictionary<string, string[]>? Shortcuts { get; set; }
	[JsonConverter(typeof(JsonStringEnumConverter))] public MouseButton SelectButton { get; set; } = MouseButton.Left;
	[JsonConverter(typeof(JsonStringEnumConverter))] public MouseButton TransformButton { get; set; } = MouseButton.Right;
	[JsonConverter(typeof(JsonStringEnumConverter))] public MouseButton PanButton { get; set; } = MouseButton.Middle;

	// The viewport's editing aids (View menu): rulers, grid and guides, and what drags snap to.
	// The grid's size and the guides themselves belong to each project.
	public bool ShowRulers { get; set; } = true;
	public bool ShowGrid { get; set; }
	public bool ShowGuides { get; set; } = true;
	public bool SnapToGrid { get; set; }
	public bool SnapToGuides { get; set; } = true;
	public bool LockGuides { get; set; }

	// Saving (Settings): a recovery copy of unsaved work every so many minutes (0 = never), and how
	// many earlier versions of a project to keep in a Backups folder next to it when saving over it
	public int AutosaveMinutes { get; set; } = 5;
	public int BackupCount { get; set; } = 3;

	// The timeline: auto key (off: posing doesn't set keys until S), only the picked's rows, and
	// playback: speed, back and forth, the picked frames over and over, and back where it
	// started when it stops
	public bool AutoKey { get; set; } = true;
	public bool OnlyPickedRows { get; set; }
	public float PlaybackSpeed { get; set; } = 1f;
	public bool PingPong { get; set; }
	public bool LoopPickedFrames { get; set; } = true;
	public bool StopGoesBack { get; set; }

	// The three must be different buttons (a hand-edited file might not have them so)
	[JsonIgnore] public bool MouseButtonsValid =>
		SelectButton != TransformButton && SelectButton != PanButton && TransformButton != PanButton;

	// (Tests point it somewhere of their own, so they never change anyone's real settings)
	public static string FilePath { get; set; } = Path.Combine(
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
