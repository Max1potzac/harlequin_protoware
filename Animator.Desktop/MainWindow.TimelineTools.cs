using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The timeline's tools beyond dragging keys about, as the Windows editor has them (Timeline,
// Events, Menus):
// - auto key (on: posing in Animate sets keys; off: it changes the pose only, until S keys it or
//   the playhead moves on), and only the picked's rows
// - playback: speed, back and forth, the picked frames over and over, back where it started
// - the right-click menus (and Keys ▾ for them): a key's curve and easing, keys moved a frame,
//   stretched, reversed, copied, deleted; every key at a frame; frames inserted or removed; the
//   picked frames as an animation sequence
// - event markers: added (E, or the menu), edited (a double-click), and their flash in the
//   viewport while playing
public sealed partial class MainWindow
{
	private static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 1.5f, 2f };

	internal readonly CheckBox AutoKeyBox = new() { Content = "Auto key" };
	internal readonly CheckBox OnlyPickedBox = new() { Content = "Only the picked's rows" };
	internal readonly ComboBox SpeedBox = new() { MinWidth = 70 };
	private readonly TextBlock _eventFlash = new()
	{
		HorizontalAlignment = HorizontalAlignment.Center,
		VerticalAlignment = VerticalAlignment.Top,
		Margin = new Thickness(0, 28, 0, 0),
		FontSize = 16,
		FontWeight = FontWeight.SemiBold,
		Foreground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(255, 210, 90)),
		IsHitTestVisible = false
	};
	private DateTime _eventFlashUntil;
	private bool _syncingTimelineTools;
	private float _playStartedAt;        // where the playhead was when play started (Stop can go back there)
	private bool _playStartedInScene;

	internal string EventFlash => _eventFlash.Text ?? "";

	// The tools on the animation bar: auto key, rows, speed, Playback ▾, Keys ▾
	private IEnumerable<Control> BuildTimelineTools()
	{
		ToolTip.SetTip(AutoKeyBox, $"On: posing in Animate sets keys. Off: posing changes the pose only, until {_keys.Describe("anim.key")} keys it (or the playhead moves on). {_keys.Describe("anim.autoKey")} switches it.");
		ToolTip.SetTip(OnlyPickedBox, "The timeline shows just the rows of what's picked");
		ToolTip.SetTip(SpeedBox, "How fast it plays here (the animation itself doesn't change)");
		AutoKeyBox.IsCheckedChanged += (_, _) => { if (!_syncingTimelineTools) SetAutoKey(AutoKeyBox.IsChecked == true); };
		OnlyPickedBox.IsCheckedChanged += (_, _) =>
		{
			if (_syncingTimelineTools) return;
			_timeline.OnlyPicked = _settings.OnlyPickedRows = OnlyPickedBox.IsChecked == true;
			SaveSettingsQuietly();
		};
		SpeedBox.ItemsSource = Speeds.Select(SpeedLabel).ToList();
		SpeedBox.SelectionChanged += (_, _) =>
		{
			if (_syncingTimelineTools || SpeedBox.SelectedIndex < 0) return;
			State.Speed = _settings.PlaybackSpeed = Speeds[SpeedBox.SelectedIndex];
			SaveSettingsQuietly();
		};
		var playback = new Button { Content = "Playback ▾" };
		playback.Click += (_, _) => PlaybackMenu().Open(playback);
		var keys = new Button { Content = "Keys ▾" };
		ToolTip.SetTip(keys, "The timeline's right-click menu: for the selected keys, or else for the frame at the playhead");
		keys.Click += (_, _) => KeysMenu(keys);
		return new Control[] { BuildCurvesBox(), AutoKeyBox, OnlyPickedBox, SpeedBox, playback, keys };
	}

	// The settings these go by (at the start)
	private void InitTimelineTools()
	{
		var s = _settings;
		State.AutoKey = s.AutoKey;
		State.PingPong = s.PingPong;
		State.Speed = Speeds.Contains(s.PlaybackSpeed) ? s.PlaybackSpeed : 1f;
		_timeline.OnlyPicked = s.OnlyPickedRows;
		_syncingTimelineTools = true;
		AutoKeyBox.IsChecked = State.AutoKey;
		OnlyPickedBox.IsChecked = _timeline.OnlyPicked;
		SpeedBox.SelectedIndex = Array.IndexOf(Speeds, State.Speed);
		_syncingTimelineTools = false;
	}

	private static string SpeedLabel(float speed) => speed.ToString("0.##", CultureInfo.CurrentCulture) + "×";

	internal void SetAutoKey(bool on)
	{
		State.AutoKey = _settings.AutoKey = on;
		SaveSettingsQuietly();
		_syncingTimelineTools = true;
		AutoKeyBox.IsChecked = on;
		_syncingTimelineTools = false;
		AnimHint(on ? "Auto key: posing in Animate sets keys." : $"Auto key off: posing holds the pose until {_keys.Describe("anim.key")} keys it.");
	}

	// ======================= playback =======================

	// Space. Stopping goes back to where play started, when that's asked for (Playback ▾).
	internal void TogglePlaying()
	{
		bool start = !State.Playing;
		if (start)
		{
			_playStartedAt = _session.ClockTime;
			_playStartedInScene = SceneLevel;
		}
		State.Playing = start;
		if (start || !_settings.StopGoesBack || _playStartedInScene != SceneLevel) return;
		if (SceneLevel) _session.SceneState.Time = _playStartedAt;
		else State.Time = _playStartedAt;
	}

	private ContextMenu PlaybackMenu()
	{
		var menu = new ContextMenu();
		void Option(string header, string tip, bool on, Action<bool> set)
		{
			var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
			ToolTip.SetTip(item, tip);
			item.Click += (_, _) =>
			{
				set(!on);
				SaveSettingsQuietly();
			};
			menu.Items.Add(item);
		}
		Option("Back and forth (ping-pong)", "Inside an object: plays to the end, then backwards to the start, and so on (a preview: the animation itself doesn't change)",
			State.PingPong, on => State.PingPong = _settings.PingPong = on);
		Option("Just the picked frames, over and over", "Inside an object: with frames picked in the timeline (drag across empty frames), play loops them alone",
			_settings.LoopPickedFrames, on => _settings.LoopPickedFrames = on);
		Option("Stopping goes back to where it started", $"{_keys.Describe("play.toggle")} to stop: the playhead returns to where play began",
			_settings.StopGoesBack, on => _settings.StopGoesBack = on);
		return menu;
	}

	// ======================= the menus =======================

	// A right-click in the keys: on keys, their menu; on an empty spot (or inside the picked
	// frames), the frame's
	private void TimelineRightClick(Vector2 p)
	{
		int frame = _timeline.FrameAt(p.X);
		(int, int)? picked = _timeline.Range is { } range && frame >= range.First && frame <= range.Last ? range : null;
		ContextMenu? menu = null;
		if (_timeline.SelectForMenu(p)) menu = KeyMenu(picked);
		else if (_timeline.IsInKeyArea(p) || picked != null) menu = FrameMenu(Math.Clamp(frame, 0, Math.Max(0, State.FrameCount - 1)), picked);
		menu?.Open(TimelineView);
	}

	// Keys ▾: the same menus, for the selected keys or the playhead's frame
	private void KeysMenu(Control under)
	{
		if (SceneLevel) return;
		if (ShowingGraph)
		{
			if (State.SelectedKeys.Count > 0) KeyMenu(null).Open(under);
			else AnimHint("Select a key in the curves first.");
			return;
		}
		var menu = State.SelectedKeys.Count > 0 ? KeyMenu(_timeline.Range) : FrameMenu(State.Frame, _timeline.Range);
		menu.Open(under);
	}

	private MenuItem Item(string header, Action click, bool enabled = true, string? command = null, string? tip = null)
	{
		var item = new MenuItem { Header = header, IsEnabled = enabled };
		if (command != null && _keys.KeysOf(command) is { Count: > 0 } keys) item.InputGesture = new KeyGesture(keys[0].Key, keys[0].Modifiers);
		if (tip != null) ToolTip.SetTip(item, tip);
		item.Click += (_, _) => click();
		return item;
	}

	// The easing presets: Bézier handles for the selected keys' stretches
	private static readonly (string Name, Bezier Curve, string Tip)[] Easings =
	{
		("Ease in", Bezier.EaseIn, "Starts slowly, ends at full speed"),
		("Ease out", Bezier.EaseOut, "Starts at full speed, slows into the next key"),
		("Ease in and out", Bezier.EaseInOut, "Slow at both ends"),
		("Overshoot", Bezier.Overshoot, "Goes past the next key's value, then settles back on it"),
		("Anticipate", Bezier.Anticipate, "Backs off first, then goes")
	};

	// On keys: the picked frames' items, an event's edit or the keys' curve, their timing, copy,
	// delete
	internal ContextMenu KeyMenu((int First, int Last)? picked)
	{
		var menu = new ContextMenu();
		if (picked is { } range) AddSequenceItems(menu, range);
		if (_timeline.SelectionIsEvents)
		{
			if (_timeline.SingleSelectedEvent is { } ev)
			{
				menu.Items.Add(Item("Edit event…", () => _ = EditEventAt(ev.Events, ev.Frame)));
				menu.Items.Add(new Separator());
			}
		}
		else
		{
			var current = _timeline.SelectedCurve();
			foreach (var curve in new[] { CurveType.Linear, CurveType.Smooth, CurveType.Stepped })
			{
				var item = new MenuItem { Header = curve.ToString(), ToggleType = MenuItemToggleType.CheckBox, IsChecked = current == curve };
				item.Click += (_, _) => _timeline.SetCurve(curve);
				menu.Items.Add(item);
			}
			var ease = new MenuItem { Header = "Bézier easing" };
			ToolTip.SetTip(ease, "A curve with handles (shown with a ring)");
			foreach (var (name, curve, tip) in Easings)
				ease.Items.Add(Item(name, () => _timeline.SetBezier(curve, name.ToLowerInvariant()), tip: tip));
			menu.Items.Add(ease);
			menu.Items.Add(new Separator());
		}
		int count = State.SelectedKeys.Count;
		menu.Items.Add(Item("A frame earlier", () => _timeline.Nudge(-1), command: "keys.earlier"));
		menu.Items.Add(Item("A frame later", () => _timeline.Nudge(+1), command: "keys.later"));
		menu.Items.Add(Item("Stretch or squeeze…", () => _ = AskStretch(), count > 1,
			tip: "Further apart or closer together, counted from the first of them (200% = twice as long)"));
		menu.Items.Add(Item("Reverse", () => _timeline.ReverseSelected(), count > 1, "keys.reverse", "Back to front, between the first and the last of them"));
		menu.Items.Add(new Separator());
		menu.Items.Add(Item("Copy", () => _timeline.Copy(), command: "edit.copy"));
		menu.Items.Add(Item("Delete", () => _timeline.DeleteSelected(), command: "edit.delete"));
		return menu;
	}

	// On an empty spot: the picked frames' items, every key at the frame, frames in and out, an
	// event, the picked's keys
	internal ContextMenu FrameMenu(int frame, (int First, int Last)? picked)
	{
		var menu = new ContextMenu();
		if (picked is { } range) AddSequenceItems(menu, range);
		menu.Items.Add(Item($"Select every key at frame {frame}", () => _timeline.SelectKeys(frame, fromOn: false), tip: "Alt+click a key does it too"));
		menu.Items.Add(Item($"Select every key from frame {frame} on", () => _timeline.SelectKeys(frame, fromOn: true)));
		menu.Items.Add(new Separator());
		bool more = State.FrameCount > 1;
		menu.Items.Add(Item($"Insert a frame after frame {frame}", () => _timeline.InsertFrames(frame, 1), command: "time.insert"));
		menu.Items.Add(Item("Insert frames…", () => _ = AskFrames($"How many frames to insert after frame {frame}:", n => _timeline.InsertFrames(frame, n))));
		menu.Items.Add(Item($"Remove frame {frame}", () => _timeline.RemoveFrames(frame, 1), more, "time.remove"));
		menu.Items.Add(Item("Remove frames…", () => _ = AskFrames($"How many frames to remove, from frame {frame} on:", n => _timeline.RemoveFrames(frame, n)), more));
		menu.Items.Add(new Separator());
		menu.Items.Add(Item($"Add event at frame {frame}…", () => _ = AddEventAt(frame), command: "anim.event"));
		int picks = State.Picked.Count;
		menu.Items.Add(Item(picks > 1 ? $"Select every key of the {picks} picked" : "Select every key of the picked one", SelectKeysOfPicked, picks > 0, "pick.keys"));
		return menu;
	}

	// The picked frames: an animation sequence of their own (split off, or copied), or let go of
	private void AddSequenceItems(ContextMenu menu, (int First, int Last) range)
	{
		bool all = range.First == 0 && range.Last >= State.FrameCount - 1;
		menu.Items.Add(Item($"Split into an animation sequence (frames {range.First}–{range.Last})…", () => _ = AskForSequence(range, split: true), !all,
			tip: "The frames become an animation of their own, starting at frame 0; here they're taken out and the frames after them move up"));
		menu.Items.Add(Item("Copy into a new animation sequence…", () => _ = AskForSequence(range, split: false), tip: "The same, but this animation keeps them"));
		menu.Items.Add(Item("Let go of the frames", () => _timeline.ClearRange(), command: "edit.back"));
		menu.Items.Add(new Separator());
	}

	private async Task AskStretch()
	{
		string? answer = await Dialogs.AskText(this, "Stretch the keys", "Their new length, as a percentage of what it is (200 = twice as long, 50 = half):", "200",
			text => TryPercent(text, out _) ? null : "A percentage above 0, like 150.");
		if (answer != null && TryPercent(answer, out float percent)) _timeline.Stretch(percent / 100f);
	}

	private static bool TryPercent(string text, out float percent)
	{
		text = text.Trim().TrimEnd('%').Trim();
		return (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out percent) ||
				float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)) && percent > 0 && percent < 10000;
	}

	private async Task AskFrames(string prompt, Action<int> then)
	{
		string? answer = await Dialogs.AskText(this, "Frames", prompt, "1",
			text => int.TryParse(text, out int n) && n > 0 && n <= 10000 ? null : "A whole number of frames, 1 or more.");
		if (answer != null && int.TryParse(answer, out int frames)) then(frames);
	}

	// ======================= events =======================

	// What the event dialog is answered with (the tests answer it); null: the user's answer
	internal Func<EventWindow, Task<bool>>? AnswerEvent { get; set; }

	internal async Task AddEventAt(int frame)
	{
		if (SceneLevel) return;
		State.Playing = false;
		var dialog = new EventWindow(frame);
		bool ok = AnswerEvent is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!ok) return;
		var e = new AnimationEvent(dialog.EventName, dialog.EventData, dialog.Volume, dialog.Pitch);
		_session.AddEvent(frame, e, dialog.PickedSound is { } sound ? StoredSoundPath(sound) : null);
	}

	internal async Task EditEventAt(EventTimeline events, int frame)
	{
		int Find() => events.Track.Keys.FindIndex(k => (int)MathF.Round(k.Time * State.Fps) == frame);
		int index = Find();
		if (index < 0) return;
		var current = events.Track.Keys[index].Value;
		var dialog = new EventWindow(frame, current.Name, current.Data, current.Volume, current.Pitch,
			Scene.Sounds.TryGetValue(current.Name, out var mapped) ? mapped : null);
		bool ok = AnswerEvent is { } answer ? await answer(dialog) : await dialog.ShowDialog<bool>(this);
		if (!ok) return;
		var updated = new AnimationEvent(dialog.EventName, dialog.EventData, dialog.Volume, dialog.Pitch);
		_session.EditEvent(events, frame, updated, dialog.PickedSound is { } sound ? StoredSoundPath(sound) : null);
	}

	// The events passed while playing, shown over the viewport for a moment
	private void FlashEvents(string text)
	{
		_eventFlash.Text = text;
		_eventFlashUntil = DateTime.UtcNow.AddSeconds(0.8);
	}

	private void UpdateEventFlash()
	{
		if (!string.IsNullOrEmpty(_eventFlash.Text) && DateTime.UtcNow > _eventFlashUntil) _eventFlash.Text = "";
	}

	private void DefineTimelineCommands()
	{
		Add("anim.graph", "_Curves / keys", () => { if (!SceneLevel) SetGraphMode(!_graphMode); });
		Add("anim.event", "Add an _event at the playhead…", () => _ = AddEventAt(State.Frame));
		Add("anim.autoKey", "_Auto key on / off", () => { if (!SceneLevel) SetAutoKey(!State.AutoKey); });
		Add("keys.earlier", "The selected keys a frame _earlier", () => { if (!SceneLevel) _timeline.Nudge(-1); });
		Add("keys.later", "The selected keys a frame _later", () => { if (!SceneLevel) _timeline.Nudge(+1); });
		Add("keys.reverse", "_Reverse the selected keys", () => { if (!SceneLevel) _timeline.ReverseSelected(); });
		Add("time.insert", "_Insert a frame after the playhead", () => { if (!SceneLevel) _timeline.InsertFrames(State.Frame, 1); });
		Add("time.remove", "Re_move the frame at the playhead", () => { if (!SceneLevel && State.FrameCount > 1) _timeline.RemoveFrames(State.Frame, 1); });
	}
}

// An event marker: its name (what the game hears), data, volume and pitch, and what it sounds
// like here (the sound map's file for its name)
public sealed class EventWindow : Window
{
	internal readonly TextBox NameBox = new() { Width = 220 };
	internal readonly TextBox DataBox = new() { Width = 220 };
	internal readonly TextBox VolumeBox = new() { Width = 70 };
	internal readonly TextBox PitchBox = new() { Width = 70 };
	private readonly TextBlock _soundText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, MaxWidth = 300 };
	internal readonly TextBlock ErrorText = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };

	public string EventName { get; private set; }
	public string? EventData { get; private set; }
	public float Volume { get; private set; }
	public float Pitch { get; private set; }

	// A sound file picked for the event's name (null: the sound map left as it is)
	public string? PickedSound { get; internal set; }

	public EventWindow(int frame, string name = "event", string? data = null, float volume = 1f, float pitch = 1f, string? sound = null)
	{
		Title = "Event";
		SizeToContent = SizeToContent.WidthAndHeight;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		EventName = name;
		EventData = data;
		Volume = volume;
		Pitch = pitch;
		NameBox.Text = name;
		DataBox.Text = data ?? "";
		VolumeBox.Text = (volume * 100).ToString("0.#", CultureInfo.CurrentCulture);
		PitchBox.Text = pitch.ToString("0.###", CultureInfo.CurrentCulture);
		_soundText.Text = sound ?? "(none: silent here)";

		static Control Row(string label, Control field, string? note = null)
		{
			var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
			row.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center });
			row.Children.Add(field);
			if (note != null) row.Children.Add(new TextBlock { Text = note, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
			return row;
		}
		var pick = new Button { Content = "Pick a sound…" };
		pick.Click += async (_, _) =>
		{
			var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
			{
				Title = "What this event sounds like",
				AllowMultiple = false,
				FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Sounds (WAV, MP3, Ogg)") { Patterns = Audio.SoundDecoder.Extensions.Select(e => "*" + e).ToArray() } }
			});
			if (files.Count == 0 || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]) is not { } path) return;
			PickedSound = path;
			_soundText.Text = path;
		};
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		ok.Click += (_, _) => TryOk();
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		cancel.Click += (_, _) => Close(false);
		Content = new StackPanel
		{
			Margin = new Thickness(14),
			Spacing = 8,
			Children =
			{
				new TextBlock { Text = $"Event at frame {frame}", FontWeight = FontWeight.SemiBold },
				Row("Name", NameBox),
				Row("Data", DataBox, "(optional)"),
				Row("Volume", VolumeBox, "%"),
				Row("Pitch", PitchBox, "1 = as recorded"),
				Row("Sound", new StackPanel { Spacing = 4, Children = { _soundText, pick } }),
				new TextBlock { Text = "The sound goes in the sound map, for every event of this name.", Foreground = Brushes.Gray, FontSize = 12 },
				ErrorText,
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
			}
		};
		Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
		UiSize.Fit(this);
	}

	private static bool TryNumber(string? text, out float value) =>
		float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
		|| float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

	internal void TryOk()
	{
		string name = (NameBox.Text ?? "").Trim();
		if (name.Length == 0) { ErrorText.Text = "An event needs a name."; return; }
		if (!TryNumber(VolumeBox.Text, out float volume) || volume < 0 || volume > 400) { ErrorText.Text = "Volume: a percentage, 0 to 400."; return; }
		if (!TryNumber(PitchBox.Text, out float pitch) || pitch < 0.25f || pitch > 4f) { ErrorText.Text = "Pitch: 0.25 to 4 (1 = as recorded)."; return; }
		ErrorText.Text = "";
		EventName = name;
		EventData = string.IsNullOrWhiteSpace(DataBox.Text) ? null : DataBox.Text.Trim();
		Volume = volume / 100f;
		Pitch = pitch;
		if (IsVisible) Close(true);   // (the tests ask it without showing it)
	}
}
