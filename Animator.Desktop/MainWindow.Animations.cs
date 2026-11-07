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
	internal readonly TextBox AnimName = new() { MinWidth = 120 };
	internal readonly TextBox AnimLength = new() { Width = 64 };
	internal readonly TextBox AnimFps = new() { Width = 52 };
	internal readonly CheckBox AnimLoop = new() { Content = "Loop" };
	private readonly Button _animDelete = new() { Content = "Delete" };
	private readonly TextBlock _animHint = new() { Foreground = Brushes.Orange, VerticalAlignment = VerticalAlignment.Center };
	private Control _animBar = null!;
	private bool _syncingAnim;

	private Control BuildAnimationsBar()
	{
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 4) };
		void Add(Control c) => bar.Children.Add(c);
		static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

		Add(new TextBlock { Text = "Animation", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
		Add(AnimList);
		var add = new Button { Content = "+ New" };
		add.Click += (_, _) => { _session.NewAnimation(); _timeline.ClearRange(); };
		Add(add);
		var copy = new Button { Content = "Duplicate" };
		copy.Click += (_, _) => { _session.DuplicateAnimation(); _timeline.ClearRange(); };
		Add(copy);
		_animDelete.Click += (_, _) => { if (_session.DeleteAnimation()) _timeline.ClearRange(); };
		Add(_animDelete);
		Add(Label("Name"));
		Add(AnimName);
		Add(Label("Frames"));
		Add(AnimLength);
		Add(Label("FPS"));
		Add(AnimFps);
		Add(AnimLoop);
		Add(_animHint);

		AnimList.SelectionChanged += (_, _) =>
		{
			if (_syncingAnim || AnimList.SelectedItem is not Animation anim || anim == State.Animation) return;
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

		_animBar = new ScrollViewer
		{
			Content = bar,
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
		};
		return _animBar;
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
			AnimList.ItemsSource = State.Animations.ToList();   // a fresh copy, so the list notices changes
			AnimList.SelectedItem = State.Animation;
			_animDelete.IsEnabled = State.Animations.Count > 1;
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
	private void ShowTimelineMenu(Vector2 p)
	{
		int frame = _timeline.FrameAt(p.X);
		if (_timeline.Range is not { } range || frame < range.First || frame > range.Last) return;

		var menu = new ContextMenu();
		bool all = range.First == 0 && range.Last >= State.FrameCount - 1;
		var split = new MenuItem { Header = $"Split into an animation sequence (frames {range.First}–{range.Last})…", IsEnabled = !all };
		split.Click += async (_, _) => await AskForSequence(range, split: true);
		var copy = new MenuItem { Header = "Copy into a new animation sequence…" };
		copy.Click += async (_, _) => await AskForSequence(range, split: false);
		var unpick = new MenuItem { Header = "Let go of the frames" };
		unpick.Click += (_, _) => _timeline.ClearRange();
		menu.Items.Add(split);
		menu.Items.Add(copy);
		menu.Items.Add(unpick);
		menu.Open(TimelineView);
	}

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
