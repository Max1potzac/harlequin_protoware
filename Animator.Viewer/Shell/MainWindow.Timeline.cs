using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Animator.Viewer;

// The timeline's tools beyond moving keys about one by one:
// - auto key (on: posing in Animate mode sets keys; off: it changes the pose only, until S keys it
//   or the playhead moves on), and S to key the pose
// - the dopesheet: only the picked's rows, a box over keys (Ctrl+drag), every key at a frame
//   (Alt+click), keys nudged, stretched or reversed, frames inserted or removed at the playhead,
//   from key to key (Shift+arrows)
// - playback: speed, back and forth, the picked frames over and over, back where it started
public partial class MainWindow
{
	private static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 1.5f, 2f };

	private float _playStartedAt;        // where the playhead was when play started (Stop goes back there)
	private bool _playStartedInScene;
	private bool _syncingTimelineTools;

	private void InitTimelineTools()
	{
		var s = _appSettings;
		_state.AutoKey = s.AutoKey;
		_state.PingPong = s.PingPong;
		_state.Speed = Speeds.Contains(s.PlaybackSpeed) ? s.PlaybackSpeed : 1f;
		_timeline.OnlyPicked = s.OnlyPickedRows;

		_syncingTimelineTools = true;
		SpeedBox.ItemsSource = Speeds.Select(SpeedLabel).ToList();
		SpeedBox.SelectedIndex = Array.IndexOf(Speeds, _state.Speed);
		AutoKeyBox.IsChecked = _state.AutoKey;
		OnlyPickedBox.IsChecked = _timeline.OnlyPicked;
		_syncingTimelineTools = false;
	}

	private static string SpeedLabel(float speed) => speed.ToString("0.##", CultureInfo.CurrentCulture) + "×";

	// ======================= auto key =======================

	private void SetAutoKey(bool on)
	{
		_state.AutoKey = on;
		_appSettings.AutoKey = on;
		_appSettings.Save();
		_syncingTimelineTools = true;
		AutoKeyBox.IsChecked = on;
		_syncingTimelineTools = false;
		RefreshBoneValues();   // its title says what typing does
		UpdateStatus();
	}

	private void AutoKey_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingTimelineTools) return;
		SetAutoKey(AutoKeyBox.IsChecked == true);
	}

	private void KeyPose_Click(object sender, RoutedEventArgs e)
	{
		KeyThePose();
		UpdateStatus();
	}

	// S: keys for the unkeyed changes (auto key off), or else for the picked bones' and parts'
	// pose as it is here. One undo step.
	private void KeyThePose()
	{
		if (_sceneLevel) return;
		if (!_state.AnimateMode)
		{
			AnimHint.Text = $"Keys are set in Animate ({KeyOf("anim.mode")}).";
			return;
		}
		_state.Playing = false;
		_state.SetFrame(_state.Frame);
		var animation = _state.Animation;
		var before = AnimationSnapshot.Capture(animation);
		bool held = _state.Hold.Any;
		int count = held
			? _state.Hold.KeyInto(animation, _state.Time)
			: PoseHold.KeyNow(animation, _state.Time, _state.PickedBones, _state.PickedSlots);
		if (count == 0)
		{
			AnimHint.Text = "Nothing to key here: pose something, or pick bones and parts.";
			return;
		}
		_state.Hold.Clear();
		AnimHint.Text = "";
		_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
			$"Key {count} {(held ? "posed" : "picked")} @ frame {_state.Frame}",
			before, AnimationSnapshot.Capture(animation), s => s.Restore()));
	}

	// Auto key off: a pose number typed in a panel changes the pose (held until S) instead of
	// setting a key. false: auto key is on, so it's for a key.
	private bool HoldInsteadOfKey(Action pose)
	{
		if (_state.AutoKey) return false;
		_state.Playing = false;
		_state.SetFrame(_state.Frame);
		var before = PoseValues.Capture(_skeleton);
		pose();
		_state.Hold.Take(before, PoseValues.Capture(_skeleton), _skeleton, _state.Animation, _state.Frame);
		_skeleton.UpdatePose();
		return true;
	}

	// ======================= rows =======================

	private void OnlyPicked_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingTimelineTools) return;
		_timeline.OnlyPicked = OnlyPickedBox.IsChecked == true;
		_appSettings.OnlyPickedRows = _timeline.OnlyPicked;
		_appSettings.Save();
		TimelineView.InvalidateVisual();
	}

	// ======================= playback =======================

	// Space. Stopping goes back to where play started, when that's asked for (Playback ▾).
	private void TogglePlaying()
	{
		bool start = !_state.Playing;
		if (start)
		{
			_playStartedAt = ClockTime;
			_playStartedInScene = _sceneLevel;
		}
		_state.Playing = start;
		if (start || !_appSettings.StopGoesBack || _playStartedInScene != _sceneLevel) return;
		if (_sceneLevel) _sceneState.Time = _playStartedAt;
		else _state.Time = _playStartedAt;
	}

	private void Speed_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingTimelineTools || SpeedBox.SelectedIndex < 0) return;
		_state.Speed = Speeds[SpeedBox.SelectedIndex];
		_appSettings.PlaybackSpeed = _state.Speed;
		_appSettings.Save();
	}

	private void PlaybackMenu_Click(object sender, RoutedEventArgs e)
	{
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		void Option(string header, string tip, bool on, Action<bool> set)
		{
			var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = on, ToolTip = tip };
			item.Click += (_, _) =>
			{
				set(item.IsChecked);
				_appSettings.Save();
			};
			menu.Items.Add(item);
		}
		Option("Back and forth (ping-pong)", "Inside an object: plays to the end, then backwards to the start, and so on (a preview: the animation itself doesn't change)",
			_state.PingPong, on => _state.PingPong = _appSettings.PingPong = on);
		Option("Just the picked frames, over and over", "Inside an object: with frames picked in the timeline (drag across empty frames), play loops them alone",
			_appSettings.LoopPickedFrames, on => _appSettings.LoopPickedFrames = on);
		Option("Stopping goes back to where it started", $"{KeyOf("play.toggle")} to stop: the playhead returns to where play began",
			_appSettings.StopGoesBack, on => _appSettings.StopGoesBack = on);
		menu.IsOpen = true;
	}

	// ======================= menu items =======================

	// The right-click menu on keys: moving them about in time
	private void AddKeyTimingItems(ContextMenu menu)
	{
		int count = _state.SelectedKeys.Count;
		var earlier = new MenuItem { Header = "A frame earlier", InputGestureText = _keys.Gesture("keys.earlier") };
		earlier.Click += (_, _) => _timeline.Nudge(-1);
		menu.Items.Add(earlier);
		var later = new MenuItem { Header = "A frame later", InputGestureText = _keys.Gesture("keys.later") };
		later.Click += (_, _) => _timeline.Nudge(+1);
		menu.Items.Add(later);
		var stretch = new MenuItem { Header = "Stretch or squeeze…", IsEnabled = count > 1,
			ToolTip = "Further apart or closer together, counted from the first of them (200% = twice as long)" };
		stretch.Click += (_, _) => AskStretch();
		menu.Items.Add(stretch);
		var reverse = new MenuItem { Header = "Reverse", IsEnabled = count > 1, InputGestureText = _keys.Gesture("keys.reverse"),
			ToolTip = "Back to front, between the first and the last of them" };
		reverse.Click += (_, _) => _timeline.ReverseSelected();
		menu.Items.Add(reverse);
		menu.Items.Add(new Separator());
	}

	private void AskStretch()
	{
		var dialog = new InputDialog("Stretch the keys", "Their new length, as a percentage of what it is (200 = twice as long, 50 = half):", "200",
			text => TryPercent(text, out _) ? null : "A percentage above 0, like 150.") { Owner = this };
		if (dialog.ShowDialog() != true || !TryPercent(dialog.Answer, out float percent)) return;
		_timeline.Stretch(percent / 100f);
	}

	private static bool TryPercent(string text, out float percent)
	{
		text = text.Trim().TrimEnd('%').Trim();
		return (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out percent) ||
				float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out percent)) && percent > 0 && percent < 10000;
	}

	// The right-click menu on an empty spot: keys at a frame, frames in and out
	private void AddFrameItems(ContextMenu menu, int frame)
	{
		var column = new MenuItem { Header = $"Select every key at frame {frame}", ToolTip = "Alt+click a key does it too" };
		column.Click += (_, _) => _timeline.SelectKeys(frame, fromOn: false);
		menu.Items.Add(column);
		var fromOn = new MenuItem { Header = $"Select every key from frame {frame} on" };
		fromOn.Click += (_, _) => _timeline.SelectKeys(frame, fromOn: true);
		menu.Items.Add(fromOn);
		menu.Items.Add(new Separator());

		var insert = new MenuItem { Header = $"Insert a frame after frame {frame}", InputGestureText = _keys.Gesture("time.insert") };
		insert.Click += (_, _) => _timeline.InsertFrames(frame, 1);
		menu.Items.Add(insert);
		var insertMany = new MenuItem { Header = "Insert frames…" };
		insertMany.Click += (_, _) => { if (AskFrames($"How many frames to insert after frame {frame}:") is { } n) _timeline.InsertFrames(frame, n); };
		menu.Items.Add(insertMany);
		var remove = new MenuItem { Header = $"Remove frame {frame}", InputGestureText = _keys.Gesture("time.remove"), IsEnabled = _state.FrameCount > 1 };
		remove.Click += (_, _) => _timeline.RemoveFrames(frame, 1);
		menu.Items.Add(remove);
		var removeMany = new MenuItem { Header = "Remove frames…", IsEnabled = _state.FrameCount > 1 };
		removeMany.Click += (_, _) => { if (AskFrames($"How many frames to remove, from frame {frame} on:") is { } n) _timeline.RemoveFrames(frame, n); };
		menu.Items.Add(removeMany);
		menu.Items.Add(new Separator());
	}

	private int? AskFrames(string prompt)
	{
		var dialog = new InputDialog("Frames", prompt, "1",
			text => int.TryParse(text, out int n) && n > 0 && n <= 10000 ? null : "A whole number of frames, 1 or more.") { Owner = this };
		return dialog.ShowDialog() == true && int.TryParse(dialog.Answer, out int frames) ? frames : null;
	}
}
