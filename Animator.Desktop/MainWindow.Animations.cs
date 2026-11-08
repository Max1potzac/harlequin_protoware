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
	internal readonly ComboBox AnimList = new() { MinWidth = 160 };
	internal readonly TextBox AnimName = new() { Width = 130 };
	internal readonly TextBox AnimLength = new() { Width = 56 };
	internal readonly TextBox AnimFps = new() { Width = 50 };
	internal readonly CheckBox AnimLoop = new() { Content = "Loop" };
	private readonly TextBlock _animHint = new() { Foreground = Brushes.Orange, VerticalAlignment = VerticalAlignment.Center };
	private bool _syncingAnim;

	// An animation in the list, by its name
	internal sealed record AnimationChoice(Animation Animation)
	{
		public override string ToString() => Animation.Name;
	}

	// The bar's animation section (see BuildTimelineBar): which animation is edited, new, a copy,
	// deleted; its name, length and looping; auto key and Key; its sound and the mixer; the curves
	// and which rows the timeline shows
	private List<Control> BuildAnimationControls()
	{
		static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
		Button B(string text, string tip, Action click)
		{
			var b = new Button { Content = text };
			ToolTip.SetTip(b, tip);
			b.Click += (_, _) => click();
			return b;
		}
		ToolTip.SetTip(AnimList, "The animation being edited");
		ToolTip.SetTip(KeyButton, $"Keys the pose at the playhead: the changes not keyed yet (auto key off), or else the picked bones' and parts' whole pose ({_keys.Describe("anim.key")})");
		KeyButton.Click += (_, _) => KeyThePose();
		var sound = B("Sound ▾", "A sound track along this animation, what events sound like (the sound map), and events and sound map files to and from the game", () => { });
		sound.Click += (_, _) => ShowSoundMenu(sound);
		var controls = new List<Control>
		{
			Label("Animation"),
			AnimList,
			B("+ New", "A new animation, empty", () => { _session.NewAnimation(); _timeline.ClearRange(); }),
			B("Duplicate", "A copy of this animation, to change", () => { _session.DuplicateAnimation(); _timeline.ClearRange(); }),
			_animDelete,
			Label("Name"),
			AnimName,
			Label("Length (frames)"),
			AnimLength,
			AnimLoop,
			AutoKeyBox,
			KeyButton,
			sound,
			B("Mixer…", "This object's animations together: on tracks over each other, and fading from one into another, with the fade times kept for the game", OpenMixer),
			_curvesBox,
			OnlyPickedBox
		};
		_animDelete.Click += (_, _) => { if (_session.DeleteAnimation()) _timeline.ClearRange(); };
		ToolTip.SetTip(_animDelete, "Deletes this animation (an object keeps at least one)");

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
		_animSection = controls;
		return controls;
	}

	private void AnimHint(string text) => _animHint.Text = text;

	private readonly Button _animDelete = new() { Content = "Delete" };
	private List<Control> _animSection = new();

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
		foreach (var c in _animSection) c.IsVisible = !SceneLevel;   // (Build puts the whole bar away)
		if (!AnimFps.IsFocused) AnimFps.Text = State.Fps.ToString();
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
			_animDelete.IsEnabled = State.Animations.Count > 1;
			AnimLoop.IsChecked = State.Animation.Loop;
		}
		finally
		{
			_syncingAnim = false;
		}
	}

	internal void CommitAnimField(TextBox box)
	{
		if (_syncingAnim || (SceneLevel && box != AnimFps)) return;   // (the frame rate is the project's: the scene's bar has it too)
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
