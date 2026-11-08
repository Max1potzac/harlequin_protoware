using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Animator.Desktop;

// The bar over the timeline: the open object's animations (which one is edited, new, a copy,
// deleted), its name, length, looping and the project's frame rate. And picked frames (a drag on
// the timeline's ruler) split or copied into an animation sequence of their own, from the
// timeline's right-click menu. The edits are EditorSession's (EditorSession.Animations.cs).
public sealed partial class MainWindow
{
	internal readonly ComboBox AnimList = new() { MinWidth = 140 };
	internal readonly TextBox AnimName = new() { MinWidth = 100 };
	internal readonly TextBox AnimLength = new() { Width = 64 };
	internal readonly TextBox AnimFps = new() { Width = 52 };
	internal readonly CheckBox AnimLoop = new() { Content = "Loop" };
	private readonly TextBlock _animHint = new() { Foreground = Brushes.Orange, VerticalAlignment = VerticalAlignment.Center };
	private Control _animBar = null!;
	private bool _syncingAnim;

	// An animation in the list, by its name
	internal sealed record AnimationChoice(Animation Animation)
	{
		public override string ToString() => Animation.Name;
	}

	private Control BuildAnimationsBar()
	{
		// Two rows: the animation itself, then playing and seeing it (each wraps when narrow)
		var first = new WrapPanel { Orientation = Orientation.Horizontal };
		var second = new WrapPanel { Orientation = Orientation.Horizontal };
		var bar = new StackPanel { Margin = new Thickness(6, 2), Children = { first, second } };
		var row = first;
		void Add(Control c)
		{
			c.Margin = new Thickness(c.Margin.Left, 2, 6, 2);
			row.Children.Add(c);
		}
		static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

		ToolTip.SetTip(AnimList, "The animation being edited");
		Add(AnimList);
		ToolTip.SetTip(_animMenu, "A new animation, a copy of this one, or this one deleted");
		_animMenu.Click += (_, _) => AnimationMenu().ShowAt(_animMenu);
		Add(_animMenu);
		Add(Label("Name"));
		Add(AnimName);
		Add(Label("Frames"));
		Add(AnimLength);
		Add(Label("FPS"));
		Add(AnimFps);
		row = second;
		Add(AnimLoop);
		var mixer = new Button { Content = "Mixer…" };
		ToolTip.SetTip(mixer, "The animations together: on tracks over each other, and fades from one to another tried out and kept");
		mixer.Click += (_, _) => OpenMixer();
		var sound = new Button { Content = "Sound ▾" };
		ToolTip.SetTip(sound, "The sound track, the sound map, events files, mute");
		sound.Click += (_, _) => ShowSoundMenu(sound);
		foreach (var tool in BuildTimelineTools()) Add(tool);
		Add(mixer);
		Add(sound);
		Add(_animHint);

		AnimList.SelectionChanged += (_, _) =>
		{
			if (_syncingAnim || AnimList.SelectedItem is not AnimationChoice { Animation: var anim } || anim == State.Animation) return;
			SwitchAnimation(anim);
		};
		AnimLoop.IsCheckedChanged += (_, _) =>
		{
			if (!_syncingAnim) _session.SetAnimationLoop(AnimLoop.IsChecked == true);
		};
		foreach (var box in new[] { AnimName, AnimLength, AnimFps })
		{
			var b = box;
			b.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { CommitAnimField(b); TimelineView.Focus(); } };
			b.LostFocus += (_, _) => CommitAnimField(b);
		}

		_animBar = bar;
		return _animBar;
	}

	private void AnimHint(string text) => _animHint.Text = text;

	private readonly Button _animMenu = new() { Content = "▾", Padding = new Thickness(8, 4) };

	// The ▾ beside the list: new, a copy, deleted
	internal MenuFlyout AnimationMenu()
	{
		var menu = new MenuFlyout();
		void Item(string header, bool enabled, Action click)
		{
			var item = new MenuItem { Header = header, IsEnabled = enabled };
			item.Click += (_, _) => click();
			menu.Items.Add(item);
		}
		Item("New animation", true, () => { _session.NewAnimation(); _timeline.ClearRange(); });
		Item($"Duplicate {State.Animation.Name}", true, () => { _session.DuplicateAnimation(); _timeline.ClearRange(); });
		Item($"Delete {State.Animation.Name}", State.Animations.Count > 1, () => { if (_session.DeleteAnimation()) _timeline.ClearRange(); });
		return menu;
	}

	// (the picked frames belonged to the one before)
	internal void SwitchAnimation(Animation animation)
	{
		_session.SwitchAnimation(animation);
		_timeline.ClearRange();
		RefreshAnimationsBar();
	}

	// After an edit or an undo (which may have taken the edited animation away), or another object
	internal void RefreshAnimationsBar()
	{
		_animBar.IsVisible = !SceneLevel;
		if (SceneLevel) return;
		if (!State.Animations.Contains(State.Animation) && State.Animations.Count > 0) _session.SwitchAnimation(State.Animations[0]);
		_syncingAnim = true;
		try
		{
			var choices = State.Animations.Select(a => new AnimationChoice(a)).ToList();   // (fresh, so the list notices changes)
			AnimList.ItemsSource = choices;
			AnimList.SelectedItem = choices.FirstOrDefault(c => c.Animation == State.Animation);
			AnimName.Text = State.Animation.Name;
			AnimLength.Text = State.FrameCount.ToString();
			AnimFps.Text = State.Fps.ToString();
			AnimLoop.IsChecked = State.Animation.Loop;
		}
		finally
		{
			_syncingAnim = false;
		}
	}

	internal void CommitAnimField(TextBox box)
	{
		if (_syncingAnim || SceneLevel) return;
		_animHint.Text = "";
		string text = box.Text?.Trim() ?? "";
		if (box == AnimName)
		{
			if (text == State.Animation.Name) return;
			if (!_session.RenameAnimation(text))
			{
				_animHint.Text = "Animation names must be unique and not empty.";
				box.Text = State.Animation.Name;
			}
		}
		else if (box == AnimLength)
		{
			if (text == State.FrameCount.ToString()) return;
			if (!int.TryParse(text, out int frames) || frames < 1 || frames > 100000)
			{
				_animHint.Text = "The length is a whole number of frames (1 or more).";
				box.Text = State.FrameCount.ToString();
				return;
			}
			_session.SetAnimationLength(frames);
		}
		else if (box == AnimFps)
		{
			if (text == State.Fps.ToString()) return;
			if (!int.TryParse(text, out int fps) || fps < 1 || fps > 240)
			{
				_animHint.Text = "FPS is a whole number from 1 to 240.";
				box.Text = State.Fps.ToString();
				return;
			}
			_session.SetFps(fps);
		}
	}

	// ======================= animation sequences =======================

	// The timeline's right-click: inside picked frames, what can be done with them
	private async Task AskForSequence((int First, int Last) range, bool split)
	{
		State.Playing = false;
		var taken = State.Animations.Select(a => a.Name).ToHashSet();
		string? name = await Dialogs.AskText(this, split ? "Split into an animation sequence" : "Copy into an animation sequence",
			$"A name for frames {range.First}–{range.Last} as an animation of their own:",
			_session.UniqueAnimationName($"{State.Animation.Name}_{range.First}-{range.Last}"),
			n => n.Length == 0 ? "It needs a name." : taken.Contains(n) ? "Another animation has that name." : null);
		if (name == null) return;
		MakeSequence(range, name, split);
	}

	internal void MakeSequence((int First, int Last) range, string name, bool split)
	{
		_session.MakeSequence(range.First, range.Last, name, split);
		_timeline.ClearRange();
		_animHint.Text = $"\"{name}\" is in the animation list ({range.Last - range.First + 1} frames).";
		RefreshAnimationsBar();
	}
}
