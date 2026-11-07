namespace Animator.Viewer.Files;

// Autosave's bookkeeping (see Recovery): whether the open document has unsaved work, and every
// few minutes a copy of it kept aside, written in the background so drawing doesn't stall. If
// the editor then closes without saving (a crash), the next start offers the copy back (Adopt).
// The editor calls Tick from a timer, Forget when the document is saved, replaced or closed, and
// says how to get back to its UI thread (Post) for when a copy is written.
public sealed class Autosaver
{
	private readonly EditorSession _session;
	private string? _file;                 // this run's copy of the open document (null: none yet)
	private DateTime _lastKept = DateTime.UtcNow;
	private Task? _keeping;                // a copy being written
	private int _version;                  // goes up on every save, new and open: a copy kept for an
	                                       // older one is thrown away once it's written
	private bool _recovered;               // opened from a copy and not saved since: unsaved, whatever
	                                       // the history says
	private bool _changedSinceKept;        // edited (or undone) since the last copy: one is worth keeping

	public Action<Action> Post { get; set; } = run => run();
	public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

	public Autosaver(EditorSession session)
	{
		_session = session;
		session.State.Undo.Changed += () => _changedSinceKept = true;
	}

	public bool Unsaved => _session.State.Undo.IsDirty || _recovered;
	public bool Keeping => _keeping != null;
	public string? CopyFile => _file;

	// Every so often: a copy kept, if there's unsaved work changed since the last one and `minutes`
	// have gone by (0: autosave is off). busy: in the middle of something (a drag, a stroke, an export).
	// True: a copy is being written.
	public bool Tick(int minutes, bool busy)
	{
		if (minutes <= 0 || _keeping != null || !Unsaved || !_changedSinceKept) return false;
		if (Now() - _lastKept < TimeSpan.FromMinutes(minutes)) return false;
		if (busy) return false;
		return Keep();
	}

	// A copy kept now, written in the background
	public bool Keep()
	{
		_lastKept = Now();
		_changedSinceKept = false;
		ProjectFile.Snapshot snapshot;
		try
		{
			_session.Scene.Fps = _session.State.Fps;
			snapshot = ProjectFile.Take(_session.Scene, _session.Assets, thumbnail: null, copyPixels: true);
		}
		catch (Exception)
		{
			return false;   // not being able to keep a copy isn't worth interrupting anyone for
		}

		string file = _file ??= Recovery.NewFile(_session.DocumentName);
		var note = Recovery.NoteForNow(_session.ProjectPath, _session.DocumentName);
		int version = _version;
		_keeping = Task.Run(() =>
		{
			try
			{
				Recovery.Keep(file, snapshot, note);
			}
			catch (Exception)
			{
				// (the next tick tries again)
			}
			finally
			{
				snapshot.Dispose();
			}
		});
		_keeping.ContinueWith(_ => Post(() =>
		{
			_keeping = null;
			if (version != _version && file != _file) Recovery.Remove(file);   // saved or replaced meanwhile
		}));
		return true;
	}

	// Saved, a new or another project opened, or closing: this run's copy isn't needed
	public void Forget(bool waitForIt = false)
	{
		_version++;
		_recovered = false;
		if (waitForIt) _keeping?.Wait(TimeSpan.FromSeconds(10));
		Recovery.Remove(_file);
		_file = null;
		_lastKept = Now();
	}

	// A copy left by a run that didn't close properly, opened (as the open document): this run
	// looks after it now, until it's saved, so it's not offered again
	public void Adopt(Recovery.Leftover left)
	{
		_file = left.File;
		_recovered = true;
		_changedSinceKept = false;   // the copy is what's open
		try { Recovery.WriteNote(left.File, Recovery.NoteForNow(left.Note.Project, left.Note.Name)); }
		catch (Exception) { }
	}
}
