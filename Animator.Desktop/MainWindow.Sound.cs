using Animator.Core.Export;
using Animator.Desktop.Audio;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Animator.Desktop;

// Sound (the animation bar's Sound ▾), as the Windows editor has it: event sounds and the
// animation's sound track heard while playing, the track's waveform behind the keys, the sound
// map, and the events and sound map files (import, export) that carry events to and from the game
// and other tools. Heard through OpenAL (Audio/); the edits are EditorSession's.
public sealed partial class MainWindow
{
	// What sounds go to (the tests listen with their own); null: the system's sound device
	internal static Func<IAudioOut>? MakeAudio { get; set; }

	private SoundPlayer? _sound;
	private readonly Dictionary<string, (float Duration, float[] Peaks)?> _waveforms = new();
	private SoundMapWindow? _soundMap;

	internal TimelinePanel KeysTimeline => _timeline;

	// (made the first time it's needed: opening the sound device takes a moment)
	internal SoundPlayer Sound => _sound ??= new SoundPlayer(MakeAudio?.Invoke() ?? OpenAlOut.Open());

	private static readonly FilePickerFileType SoundType = new("Sounds (WAV, MP3, Ogg)") { Patterns = SoundDecoder.Extensions.Select(e => "*" + e).ToArray() };
	private static readonly FilePickerFileType EventsType = new("Events files (.events.json)") { Patterns = new[] { "*.events.json", "*.json" } };
	private static readonly FilePickerFileType SoundMapType = new("Sound maps (.sounds.json)") { Patterns = new[] { "*.sounds.json", "*.json" } };

	// A file picked → how it's kept: relative to the project's folder when on the same drive
	private string StoredSoundPath(string file) => _session.StoredSoundPath(file);

	private void PlayEventSound(AnimationEvent e)
	{
		if (Scene.Sounds.TryGetValue(e.Name, out var path)) Sound.Play(ResolveSound(path), e.Volume, e.Pitch);
	}

	// While playing: the events the playhead went past (the stretches of time, see
	// EditorState.Advance) sound, and are named in the status bar
	private void FireEvents(IReadOnlyList<(float From, float To)> passed)
	{
		if (State.Animation.Events is not { } events) return;
		var fired = passed.SelectMany(span => events.Between(span.From, span.To)).ToList();
		foreach (var e in fired) PlayEventSound(e);
		if (fired.Count > 0) Hint("⚑ " + string.Join("    ⚑ ", fired.Select(e => e.Data == null ? e.Name : $"{e.Name} ({e.Data})")));
	}

	// Every frame: the sound track in step with the playhead, and its waveform under the timeline
	private void SyncSound()
	{
		var track = !SceneLevel ? State.Animation.Sound : null;
		string? file = track != null ? ResolveSound(track.Path) : null;
		bool playing = State.Playing && !SceneLevel;
		if (file != null || _sound != null)
			Sound.SyncTrack(file, track?.Volume ?? 1f, State.Time - (track?.Offset ?? 0f), playing, State.Speed);
		_timeline.Waveform = file != null && Waveform(file) is { } wave ? (wave.Peaks, wave.Duration, track!.Offset) : null;
	}

	private (float Duration, float[] Peaks)? Waveform(string file)
	{
		if (_waveforms.TryGetValue(file, out var known)) return known;
		(float, float[])? read = null;
		try
		{
			if (File.Exists(file) && Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase))
			{
				using var stream = File.OpenRead(file);
				read = WavPeaks.Read(stream, 4000);
			}
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
		return _waveforms[file] = read;
	}

	// ======================= the menu =======================

	private void ShowSoundMenu(Control under)
	{
		var menu = new MenuFlyout();
		void Add(string header, Action click, bool enabled = true, string? tip = null)
		{
			var item = new MenuItem { Header = header, IsEnabled = enabled };
			if (tip != null) ToolTip.SetTip(item, tip);
			item.Click += (_, _) => click();
			menu.Items.Add(item);
		}
		bool inObject = !SceneLevel;
		var track = inObject ? State.Animation.Sound : null;
		Add(track == null ? "Sound track…" : $"Sound track: {Path.GetFileName(track.Path)}…", () => _ = PickSoundTrack(), inObject,
			"A sound playing along this animation (a voice line, music), to time it to. WAV files show their waveform under the keys.");
		Add("Sound track starts at frame…", () => _ = SoundTrackOffset(), track != null);
		Add("Sound track volume…", () => _ = SoundTrackVolume(), track != null);
		Add("Remove the sound track", () => _session.SetSoundTrack(null, "Remove the sound track"), track != null);
		menu.Items.Add(new Separator());
		Add("Sound map…", OpenSoundMap, true, "What each event name sounds like in the editor");
		Add("Import events…", () => _ = ImportEvents(), inObject, "An events file (.events.json) into this object's animations of the same names");
		Add("Export events…", () => _ = ExportEvents(), inObject, "This object's animations' events (and sound tracks) as an events file");
		Add("Import a sound map…", () => _ = ImportSoundMap());
		Add("Export the sound map…", () => _ = ExportSoundMap(), Scene.Sounds.Count > 0);
		menu.Items.Add(new Separator());
		var mute = new MenuItem { Header = "Mute", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _sound?.Muted ?? false };
		mute.Click += (_, _) => SetMuted(!(_sound?.Muted ?? false));
		menu.Items.Add(mute);
		menu.ShowAt(under);
	}

	internal void SetMuted(bool muted)
	{
		Sound.Muted = muted;
		if (muted) Sound.StopAll();
	}

	private async Task<string?> PickFile(string title, FilePickerFileType type)
	{
		var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = title,
			AllowMultiple = false,
			FileTypeFilter = new[] { type, FilePickerFileTypes.All }
		});
		return files.Count > 0 ? files[0].TryGetLocalPath() : null;
	}

	private async Task<string?> PickSaveFile(string title, FilePickerFileType type, string suggested)
	{
		var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = title,
			SuggestedFileName = suggested,
			FileTypeChoices = new[] { type },
			ShowOverwritePrompt = true
		});
		return file?.TryGetLocalPath();
	}

	// ======================= the sound track =======================

	private async Task PickSoundTrack()
	{
		if (SceneLevel) return;
		if (await PickFile($"A sound track for {State.Animation.Name}", SoundType) is { } file) SetSoundTrackFile(file);
	}

	// (the tests give the file this way)
	internal void SetSoundTrackFile(string file)
	{
		var old = State.Animation.Sound;
		_session.SetSoundTrack(new SoundTrack(StoredSoundPath(file), old?.Offset ?? 0f, old?.Volume ?? 1f), $"Sound track {Path.GetFileName(file)}");
		float length = Waveform(file)?.Duration ?? (float)(Sound.Sound(file)?.Duration ?? 0);
		if (length > State.Animation.Duration + 1e-3f)
			AnimHint($"The sound is {length:0.##} s long, the animation {State.Animation.Duration:0.##} s: lengthen it to hear all of it.");
	}

	private async Task SoundTrackOffset()
	{
		if (State.Animation.Sound is not { } track) return;
		string? answer = await Dialogs.AskText(this, "Sound track", "The frame the sound starts on (negative: it started before the animation):",
			MathF.Round(track.Offset * State.Fps).ToString(), text => int.TryParse(text, out _) ? null : "A whole number of frames.");
		if (answer == null || !int.TryParse(answer, out int frame)) return;
		_session.SetSoundTrack(track with { Offset = frame / (float)State.Fps }, $"Sound track from frame {frame}");
	}

	private async Task SoundTrackVolume()
	{
		if (State.Animation.Sound is not { } track) return;
		string? answer = await Dialogs.AskText(this, "Sound track", "Its volume, in % (100 = as recorded):", MathF.Round(track.Volume * 100).ToString(),
			text => int.TryParse(text, out int v) && v >= 0 && v <= 100 ? null : "0 to 100.");
		if (answer == null || !int.TryParse(answer, out int percent)) return;
		_session.SetSoundTrack(track with { Volume = percent / 100f }, $"Sound track volume {percent}%");
	}

	// ======================= the sound map =======================

	internal SoundMapWindow OpenSoundMap(bool show)
	{
		if (_soundMap != null)
		{
			if (show) _soundMap.Activate();
			return _soundMap;
		}
		_soundMap = new SoundMapWindow(Scene.Sounds,
			(name, change) => _session.EditScene(() => change() ? name : null),
			StoredSoundPath,
			() => Scene.Objects.SelectMany(o => o.Animations).SelectMany(a => a.Events?.Track.Keys ?? new List<Keyframe<AnimationEvent>>()).Select(k => k.Value.Name));
		_soundMap.Closed += (_, _) => _soundMap = null;
		if (show) _soundMap.Show(this);
		return _soundMap;
	}

	private void OpenSoundMap() => OpenSoundMap(show: true);

	// ======================= files =======================

	// What "replace or add?" is answered with (the tests answer it): true replace, false add, null cancel
	internal Func<Task<bool?>>? AnswerReplaceEvents { get; set; }

	private async Task ImportEvents()
	{
		if (SceneLevel) return;
		if (await PickFile("Import events", EventsType) is not { } path) return;
		bool? replace = AnswerReplaceEvents is { } answer ? await answer() : await Dialogs.Ask(this, "Import events",
			"Replace each animation's events with the file's, or add the file's to them (one on a frame that has one replaces it)?",
			"Replace", "Add", "Cancel") switch { "Replace" => true, "Add" => false, _ => null };
		if (replace == null) return;
		await ImportEventsFrom(path, replace.Value);
	}

	internal async Task ImportEventsFrom(string path, bool replace)
	{
		EventsFile file;
		try { file = EventsFile.FromJson(File.ReadAllText(path)); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			await Dialogs.Tell(this, "Import events", $"That events file can't be read:\n{ex.Message}");
			return;
		}
		// (sound track paths in the file are relative to it: kept relative to the project instead)
		var merged = _session.ImportEvents(file, Path.GetDirectoryName(path) ?? "", replace, $"Import events from {Path.GetFileName(path)}");
		if (merged is not { } m) return;
		AnimHint($"Events: {m.Added} added, {m.Replaced} replaced" + (m.SoundTracks > 0 ? $", {m.SoundTracks} sound track(s)" : "")
			+ (m.Missing.Count > 0 ? $". No animation called {string.Join(", ", m.Missing)} here." : "."));
	}

	private async Task ExportEvents()
	{
		if (SceneLevel || _session.Active is not { } obj) return;
		if (await PickSaveFile("Export events", EventsType, $"{obj.Name}.events.json") is { } path) await ExportEventsTo(path);
	}

	internal async Task ExportEventsTo(string path)
	{
		if (_session.Active is not { } obj) return;
		var file = EventsFile.Of(obj.Name, State.Animations, State.Fps);
		try { File.WriteAllText(path, file.ToJson()); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await Dialogs.Tell(this, "Export events", $"It can't be written there:\n{ex.Message}");
			return;
		}
		AnimHint($"Wrote the events of {file.Animations.Count} animation(s).");
	}

	private async Task ImportSoundMap()
	{
		if (await PickFile("Import a sound map", SoundMapType) is { } path) await ImportSoundMapFrom(path);
	}

	internal async Task ImportSoundMapFrom(string path)
	{
		SoundMapFile file;
		try { file = SoundMapFile.FromJson(File.ReadAllText(path)); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			await Dialogs.Tell(this, "Import a sound map", $"That sound map can't be read:\n{ex.Message}");
			return;
		}
		_session.ImportSoundMap(file, Path.GetDirectoryName(path) ?? "", $"Import the sound map {Path.GetFileName(path)}");
		AnimHint($"{file.Sounds.Count} sound(s) in the map.");
	}

	private async Task ExportSoundMap()
	{
		if (await PickSaveFile("Export the sound map", SoundMapType, "sounds.sounds.json") is { } path) await ExportSoundMapTo(path);
	}

	internal async Task ExportSoundMapTo(string path)
	{
		string folder = Path.GetDirectoryName(path) ?? "";
		var file = new SoundMapFile();
		foreach (var (name, stored) in Scene.Sounds)
		{
			string full = ResolveSound(stored);
			file.Sounds[name] = string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)
				? Path.GetRelativePath(folder, full) : full;   // relative to the map file
		}
		try { File.WriteAllText(path, file.ToJson()); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await Dialogs.Tell(this, "Export the sound map", $"It can't be written there:\n{ex.Message}");
		}
	}
}
