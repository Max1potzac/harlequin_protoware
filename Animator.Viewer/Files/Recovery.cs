using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Animator.Viewer.Files;

// Copies of unsaved work, kept every few minutes (Settings, Saving) in %LocalAppData%\Harlequin\
// Recovery, each with a note: which project it's a copy of, when it was kept, and which run of
// Harlequin kept it. Saving, or closing without saving, throws the copy away; so a copy whose run
// is over means Harlequin didn't close properly, and it's offered back at the next start.
public static class Recovery
{
	public static string Folder { get; set; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Harlequin", "Recovery");

	public sealed class Note
	{
		public string? Project { get; set; }     // the file it's a copy of (null: one never saved)
		public string Name { get; set; } = "";   // what the title bar called it
		public DateTime KeptUtc { get; set; }
		public int ProcessId { get; set; }       // the run that kept it...
		public DateTime ProcessStartUtc { get; set; }   // ...told apart from a later one with the same id
	}

	public sealed record Leftover(string File, Note Note);

	// A new file for this run's copy of a document
	public static string NewFile(string name)
	{
		var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
		if (safe.Length > 40) safe = safe[..40];
		return Path.Combine(Folder, $"{safe} {Guid.NewGuid().ToString("N")[..8]}{ProjectFile.Extension}");
	}

	public static Note NoteForNow(string? project, string name)
	{
		using var me = Process.GetCurrentProcess();
		return new Note
		{
			Project = project,
			Name = name,
			KeptUtc = DateTime.UtcNow,
			ProcessId = me.Id,
			ProcessStartUtc = me.StartTime.ToUniversalTime()
		};
	}

	private static string NoteFile(string file) => file + ".json";

	// Writes a copy and then its note (the folder made first, the first time). Safe on another
	// thread: the snapshot has its own pixels.
	public static void Keep(string file, ProjectFile.Snapshot snapshot, Note note)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		ProjectFile.Write(file, snapshot);
		WriteNote(file, note);
	}

	public static void WriteNote(string file, Note note)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(NoteFile(file), JsonSerializer.Serialize(note));
	}

	// The copy and its note gone (quietly: a copy that can't be thrown away isn't worth an error)
	public static void Remove(string? file)
	{
		if (file == null) return;
		foreach (var path in new[] { file, NoteFile(file), file + ".saving" })
		{
			try { if (File.Exists(path)) File.Delete(path); }
			catch (Exception) { }
		}
	}

	// Copies left by runs that are over, the newest first (notes without their copy are cleared away)
	public static List<Leftover> Leftovers()
	{
		var found = new List<Leftover>();
		if (!Directory.Exists(Folder)) return found;
		foreach (var notePath in Directory.GetFiles(Folder, "*" + ProjectFile.Extension + ".json"))
		{
			string file = notePath[..^".json".Length];
			Note? note;
			try { note = JsonSerializer.Deserialize<Note>(File.ReadAllText(notePath)); }
			catch (Exception) { note = null; }
			if (note == null || !File.Exists(file))
			{
				Remove(file);
				continue;
			}
			if (StillRunning(note)) continue;   // another Harlequin, open now: its copy is its own
			found.Add(new Leftover(file, note));
		}
		return found.OrderByDescending(l => l.Note.KeptUtc).ToList();
	}

	private static bool StillRunning(Note note)
	{
		try
		{
			using var process = Process.GetProcessById(note.ProcessId);
			return Math.Abs((process.StartTime.ToUniversalTime() - note.ProcessStartUtc).TotalSeconds) < 2;
		}
		catch (Exception)
		{
			return false;   // no such process (any more), or not one we may look at
		}
	}
}
