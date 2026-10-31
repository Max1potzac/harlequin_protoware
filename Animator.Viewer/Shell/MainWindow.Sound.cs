using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Animator.Viewer.Audio;
using Microsoft.Win32;

namespace Animator.Viewer;

// Sound (the timeline bar's Sound ▾): event sounds and the animation's sound track heard while
// playing, the track's waveform behind the keys, the sound map, and the events and sound map
// files (import, export) that carry events to and from the game and other tools.
public partial class MainWindow
{
	private readonly SoundPlayer _sound = new();
	private readonly Dictionary<string, (float Duration, float[] Peaks)?> _waveforms = new();
	private SoundMapWindow? _soundMap;

	private string? ProjectFolder => _projectPath != null ? Path.GetDirectoryName(_projectPath) : null;

	// A path the sound map (or a sound track) keeps → the file
	private string ResolveSound(string stored) =>
		Path.IsPathRooted(stored) ? stored : Path.GetFullPath(Path.Combine(ProjectFolder ?? Environment.CurrentDirectory, stored));

	// A file picked → how it's kept: relative to the project's folder when on the same drive
	private string StoredSoundPath(string file)
	{
		if (ProjectFolder is not { } folder || !string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(file), StringComparison.OrdinalIgnoreCase))
			return file;
		return Path.GetRelativePath(folder, file);
	}

	private void PlayEventSound(AnimationEvent e)
	{
		if (_scene.Sounds.TryGetValue(e.Name, out var path)) _sound.Play(ResolveSound(path), e.Volume, e.Pitch);
	}

	// Every frame: the sound track in step with the playhead, and its waveform under the timeline
	private void SyncSound()
	{
		var track = !_sceneLevel ? _state.Animation.Sound : null;
		string? file = track != null ? ResolveSound(track.Path) : null;
		bool playing = _state.Playing && !_sceneLevel && !_mixing;
		_sound.SyncTrack(file, track?.Volume ?? 1f, _state.Time - (track?.Offset ?? 0f), playing, _state.Speed);
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

	private void SoundMenu_Click(object sender, RoutedEventArgs e)
	{
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		void Add(string header, Action click, bool enabled = true, string? tip = null)
		{
			var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
			item.Click += (_, _) => click();
			menu.Items.Add(item);
		}
		bool inObject = !_sceneLevel;
		var track = inObject ? _state.Animation.Sound : null;
		Add(track == null ? "Sound track…" : $"Sound track: {Path.GetFileName(track.Path)}…", PickSoundTrack, inObject,
			"A sound playing along this animation (a voice line, music), to time it to. WAV files show their waveform under the keys.");
		Add("Sound track starts at frame…", SoundTrackOffset, track != null);
		Add("Sound track volume…", SoundTrackVolume, track != null);
		Add("Remove the sound track", () => SetSoundTrack(null, "Remove the sound track"), track != null);
		menu.Items.Add(new Separator());
		Add("Sound map…", OpenSoundMap, true, "What each event name sounds like in the editor");
		Add("Import events…", ImportEvents, inObject, "An events file (.events.json) into this object's animations of the same names");
		Add("Export events…", ExportEvents, inObject, "This object's animations' events (and sound tracks) as an events file");
		Add("Import a sound map…", ImportSoundMap);
		Add("Export the sound map…", ExportSoundMap, _scene.Sounds.Count > 0);
		menu.Items.Add(new Separator());
		var mute = new MenuItem { Header = "Mute", IsCheckable = true, IsChecked = _sound.Muted };
		mute.Click += (_, _) =>
		{
			_sound.Muted = mute.IsChecked;
			if (_sound.Muted) _sound.StopAll();
		};
		menu.Items.Add(mute);
		menu.IsOpen = true;
	}

	private void SetSoundTrack(SoundTrack? track, string name)
	{
		var animation = _state.Animation;
		_state.EditStructure(() =>
		{
			if (animation.Sound == track) return null;
			animation.Sound = track;
			return name;
		});
	}

	private void PickSoundTrack()
	{
		var dialog = new OpenFileDialog
		{
			Title = $"A sound track for {_state.Animation.Name}",
			Filter = "Sounds (*.wav;*.mp3;*.wma;*.m4a;*.aac)|*.wav;*.mp3;*.wma;*.m4a;*.aac|All files (*.*)|*.*"
		};
		if (dialog.ShowDialog(this) != true) return;
		var old = _state.Animation.Sound;
		SetSoundTrack(new SoundTrack(StoredSoundPath(dialog.FileName), old?.Offset ?? 0f, old?.Volume ?? 1f), $"Sound track {Path.GetFileName(dialog.FileName)}");
		if (Waveform(dialog.FileName) is { } wave && wave.Duration > _state.Animation.Duration + 1e-3f)
			AnimHint.Text = $"The sound is {wave.Duration:0.##} s long, the animation {_state.Animation.Duration:0.##} s: lengthen it to hear all of it.";
	}

	private void SoundTrackOffset()
	{
		if (_state.Animation.Sound is not { } track) return;
		var dialog = new InputDialog("Sound track", "The frame the sound starts on (negative: it started before the animation):",
			MathF.Round(track.Offset * _state.Fps).ToString(),
			text => int.TryParse(text, out _) ? null : "A whole number of frames.") { Owner = this };
		if (dialog.ShowDialog() != true || !int.TryParse(dialog.Answer, out int frame)) return;
		SetSoundTrack(track with { Offset = frame / (float)_state.Fps }, $"Sound track from frame {frame}");
	}

	private void SoundTrackVolume()
	{
		if (_state.Animation.Sound is not { } track) return;
		var dialog = new InputDialog("Sound track", "Its volume, in % (100 = as recorded):", MathF.Round(track.Volume * 100).ToString(),
			text => int.TryParse(text, out int v) && v >= 0 && v <= 100 ? null : "0 to 100.") { Owner = this };
		if (dialog.ShowDialog() != true || !int.TryParse(dialog.Answer, out int percent)) return;
		SetSoundTrack(track with { Volume = percent / 100f }, $"Sound track volume {percent}%");
	}

	private void OpenSoundMap()
	{
		if (_soundMap != null)
		{
			_soundMap.Activate();
			return;
		}
		_soundMap = new SoundMapWindow(_scene.Sounds,
			(name, change) => EditScene(() => change() ? name : null),
			StoredSoundPath,
			() => _scene.Objects.SelectMany(o => o.Animations).SelectMany(a => a.Events?.Track.Keys ?? new List<Keyframe<AnimationEvent>>()).Select(k => k.Value.Name))
		{ Owner = this };
		_soundMap.Closed += (_, _) => _soundMap = null;
		_soundMap.Show();
	}

	// ======================= files =======================

	private const string EventsFilter = "Events files (*.events.json)|*.events.json|JSON files (*.json)|*.json|All files (*.*)|*.*";
	private const string SoundMapFilter = "Sound maps (*.sounds.json)|*.sounds.json|JSON files (*.json)|*.json|All files (*.*)|*.*";

	private void ImportEvents()
	{
		if (_sceneLevel) return;
		var dialog = new OpenFileDialog { Title = "Import events", Filter = EventsFilter };
		if (dialog.ShowDialog(this) != true) return;
		EventsFile file;
		try { file = EventsFile.FromJson(File.ReadAllText(dialog.FileName)); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			MessageBox.Show(this, $"That events file can't be read:\n{ex.Message}", "Import events");
			return;
		}
		var answer = MessageBox.Show(this,
			"Replace each animation's events with the file's?\n\nYes: replace them.  No: add the file's to them (one on a frame that has one replaces it).",
			"Import events", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
		if (answer == MessageBoxResult.Cancel) return;

		// Sound track paths in the file are relative to it: kept relative to the project instead
		string folder = Path.GetDirectoryName(dialog.FileName) ?? "";
		foreach (var a in file.Animations)
			if (a.Sound is { Path.Length: > 0 } s && !Path.IsPathRooted(s.Path))
				s.Path = StoredSoundPath(Path.GetFullPath(Path.Combine(folder, s.Path)));

		EventsFile.Merged? merged = null;
		_state.EditStructure(() =>
		{
			merged = file.MergeInto(_state.Animations, _state.Fps, replace: answer == MessageBoxResult.Yes);
			return merged.Added + merged.Replaced + merged.SoundTracks > 0 ? $"Import events from {Path.GetFileName(dialog.FileName)}" : null;
		});
		if (merged is not { } m) return;
		AnimHint.Text = $"Events: {m.Added} added, {m.Replaced} replaced" + (m.SoundTracks > 0 ? $", {m.SoundTracks} sound track(s)" : "")
			+ (m.Missing.Count > 0 ? $". No animation called {string.Join(", ", m.Missing)} here." : ".");
	}

	private void ExportEvents()
	{
		if (_sceneLevel || _active is not { } obj) return;
		var dialog = new SaveFileDialog { Title = "Export events", Filter = EventsFilter, FileName = $"{obj.Name}.events.json" };
		if (dialog.ShowDialog(this) != true) return;
		var file = EventsFile.Of(obj.Name, _state.Animations, _state.Fps);
		try { File.WriteAllText(dialog.FileName, file.ToJson()); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			MessageBox.Show(this, $"It can't be written there:\n{ex.Message}", "Export events");
			return;
		}
		AnimHint.Text = $"Wrote the events of {file.Animations.Count} animation(s).";
	}

	private void ImportSoundMap()
	{
		var dialog = new OpenFileDialog { Title = "Import a sound map", Filter = SoundMapFilter };
		if (dialog.ShowDialog(this) != true) return;
		SoundMapFile file;
		try { file = SoundMapFile.FromJson(File.ReadAllText(dialog.FileName)); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			MessageBox.Show(this, $"That sound map can't be read:\n{ex.Message}", "Import a sound map");
			return;
		}
		string folder = Path.GetDirectoryName(dialog.FileName) ?? "";
		EditScene(() =>
		{
			foreach (var (name, path) in file.Sounds)
				if (name.Trim().Length > 0 && path.Trim().Length > 0)
					_scene.Sounds[name.Trim()] = StoredSoundPath(Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(folder, path)));
			return file.Sounds.Count > 0 ? $"Import the sound map {Path.GetFileName(dialog.FileName)}" : null;
		});
		AnimHint.Text = $"{file.Sounds.Count} sound(s) in the map.";
	}

	private void ExportSoundMap()
	{
		var dialog = new SaveFileDialog { Title = "Export the sound map", Filter = SoundMapFilter, FileName = "sounds.sounds.json" };
		if (dialog.ShowDialog(this) != true) return;
		string folder = Path.GetDirectoryName(dialog.FileName) ?? "";
		var file = new SoundMapFile();
		foreach (var (name, path) in _scene.Sounds)
		{
			string full = ResolveSound(path);
			file.Sounds[name] = string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)
				? Path.GetRelativePath(folder, full) : full;   // relative to the map file
		}
		try { File.WriteAllText(dialog.FileName, file.ToJson()); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			MessageBox.Show(this, $"It can't be written there:\n{ex.Message}", "Export the sound map");
		}
	}
}
