using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Animator.Viewer;

public partial class MainWindow
{
	private bool _syncingAnim;

	private void InitAnimationsPanel()
	{
		_state.Undo.Changed += RefreshAnimationsPanel;   // add/delete/rename can all be undone
		RefreshAnimationsPanel();
	}

	private void RefreshAnimationsPanel()
	{
		if (_state.Animations.Count == 0)
			_state.Animations.Add(new Animation { Name = "animation", Duration = 2f, Loop = true });

		// An undo may have removed the animation being edited
		if (!_state.Animations.Contains(_state.Animation))
			SwitchAnimation(_state.Animations[0]);

		_syncingAnim = true;
		AnimList.ItemsSource = _state.Animations.ToList();   // a fresh copy, so the dropdown notices changes
		AnimList.SelectedItem = _state.Animation;
		AnimDeleteButton.IsEnabled = _state.Animations.Count > 1;
		SetField(AnimName, _state.Animation.Name);
		SetField(AnimLength, _state.FrameCount.ToString());
		SetField(AnimFps, _state.Fps.ToString());
		AnimLoop.IsChecked = _state.Animation.Loop;
		_syncingAnim = false;
	}

	private void SwitchAnimation(Animation animation)
	{
		_state.Animation = animation;
		_state.SelectedKeys.Clear();   // selected keys (and picked frames) belong to the old animation
		_timeline.ClearRange();
		_state.Time = 0;
		_state.Playing = false;
	}

	private string UniqueAnimationName(string baseName)
	{
		var used = _state.Animations.Select(a => a.Name).ToHashSet();
		if (!used.Contains(baseName)) return baseName;
		for (int i = 2; ; i++)
		{
			string candidate = $"{baseName}{i}";
			if (!used.Contains(candidate)) return candidate;
		}
	}

	// ---------- animation sequences ----------

	// The picked frames' menu items: split them off into an animation sequence, or copy them into one
	private void AddSequenceItems(ContextMenu menu, (int First, int Last) range)
	{
		bool all = range.First == 0 && range.Last >= _state.FrameCount - 1;
		var split = new MenuItem
		{
			Header = $"Split into animation sequence (frames {range.First}–{range.Last})…",
			IsEnabled = !all,   // nothing would be left of this one: copy it, or rename it
			ToolTip = "The frames become an animation of their own, starting at frame 0; here they're taken out and the frames after them move up"
		};
		split.Click += (_, _) => MakeSequence(range, split: true);
		menu.Items.Add(split);
		var copy = new MenuItem { Header = "Copy into a new animation sequence…", ToolTip = "The same, but this animation keeps them" };
		copy.Click += (_, _) => MakeSequence(range, split: false);
		menu.Items.Add(copy);
		var unpick = new MenuItem { Header = "Let go of the frames", InputGestureText = "Esc" };
		unpick.Click += (_, _) => { _timeline.ClearRange(); TimelineView.InvalidateVisual(); };
		menu.Items.Add(unpick);
		menu.Items.Add(new Separator());
	}

	// The picked frames as an animation sequence of their own, named in a dialog. Split off: they
	// leave this animation (the frames after them move up). Staying on this animation, more can be
	// split off one after another.
	private void MakeSequence((int First, int Last) range, bool split)
	{
		_state.Playing = false;
		var source = _state.Animation;
		int fps = _state.Fps;
		var taken = _state.Animations.Select(a => a.Name).ToHashSet();
		var dialog = new SequenceDialog(source.Name, range.First, range.Last, split,
			UniqueAnimationName($"{source.Name}_{range.First}-{range.Last}"), taken) { Owner = this };
		if (dialog.ShowDialog() != true) return;

		string name = dialog.SequenceName;
		_state.EditStructure(() =>
		{
			var piece = split
				? AnimationOps.Split(source, range.First, range.Last, fps, name)
				: AnimationOps.Extract(source, range.First, range.Last, fps, name);
			_state.Animations.Insert(_state.Animations.IndexOf(source) + 1, piece);
			return $"{(split ? "Split" : "Copy")} frames {range.First}–{range.Last} of {source.Name} into {name}";
		});
		_timeline.ClearRange();
		if (split) _state.SetFrame(Math.Min(range.First, _state.FrameCount));   // where they were
		AnimHint.Text = $"\"{name}\" is in the animation list ({range.Last - range.First + 1} frames).";
		TimelineView.InvalidateVisual();
	}

	// ---------- list & buttons ----------

	private void AnimList_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingAnim || AnimList.SelectedItem is not Animation anim || anim == _state.Animation) return;
		SwitchAnimation(anim);
		RefreshAnimationsPanel();
		UpdateStatus();
	}

	private void AnimNew_Click(object sender, RoutedEventArgs e) =>
		_state.EditStructure(() =>
		{
			var anim = new Animation { Name = UniqueAnimationName("animation"), Duration = 2f, Loop = true };
			_state.Animations.Add(anim);
			SwitchAnimation(anim);
			return $"New animation {anim.Name}";
		});

	private void AnimDuplicate_Click(object sender, RoutedEventArgs e)
	{
		var source = _state.Animation;
		_state.EditStructure(() =>
		{
			var copy = ProjectMapper.CloneAnimation(source, UniqueAnimationName($"{source.Name}_copy"));
			_state.Animations.Insert(_state.Animations.IndexOf(source) + 1, copy);
			SwitchAnimation(copy);
			return $"Duplicate {source.Name}";
		});
	}

	private void AnimDelete_Click(object sender, RoutedEventArgs e)
	{
		if (_state.Animations.Count <= 1) return;   // a project always keeps at least one
		var anim = _state.Animation;
		_state.EditStructure(() =>
		{
			int i = _state.Animations.IndexOf(anim);
			_state.Animations.Remove(anim);
			SwitchAnimation(_state.Animations[Math.Min(i, _state.Animations.Count - 1)]);
			return $"Delete animation {anim.Name}";
		});
	}

	private void AnimLoop_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingAnim) return;
		var anim = _state.Animation;
		bool loop = AnimLoop.IsChecked == true;
		_state.EditStructure(() =>
		{
			anim.Loop = loop;
			return $"{anim.Name} {(loop ? "loops" : "plays once")}";
		});
	}

	// ---------- text fields ----------

	private void AnimField_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitAnimField((TextBox)sender);
		TimelineView.Focus();
	}

	private void AnimField_LostFocus(object sender, RoutedEventArgs e) => CommitAnimField((TextBox)sender);

	private void CommitAnimField(TextBox box)
	{
		if (_syncingAnim) return;
		var anim = _state.Animation;
		AnimHint.Text = "";

		switch ((string)box.Tag)
		{
			case "Name":
				{
					string name = box.Text.Trim();
					if (name == anim.Name) return;
					if (name.Length == 0 || _state.Animations.Any(a => a != anim && a.Name == name))
					{
						AnimHint.Text = "Animation names must be unique and not empty.";
						box.Text = anim.Name;
						return;
					}
					// The object's preview names its animation, so it follows (clips point at the animation itself)
					string oldName = anim.Name;
					EditStructureAndScene(() =>
					{
						anim.Name = name;
						if (_active != null && _active.PreviewAnimation == oldName) _active.PreviewAnimation = name;
						return $"Rename animation to {name}";
					});
					break;
				}

			case "Length":
				{
					if (!int.TryParse(box.Text.Trim(), out int frames) || frames < 1 || frames > 100000)
					{
						AnimHint.Text = "Length must be a whole number of frames (1 or more).";
						box.Text = _state.FrameCount.ToString();
						return;
					}
					if (frames == _state.FrameCount) return;
					float duration = frames / (float)_state.Fps;
					_state.EditStructure(() =>
					{
						anim.Duration = duration;
						return $"{anim.Name} length: {frames} frames";
					});
					_state.SetFrame(_state.Frame);   // keeps the playhead inside the new length
					break;
				}

			case "Fps":
				{
					if (!int.TryParse(box.Text.Trim(), out int fps) || fps < 1 || fps > 240)
					{
						AnimHint.Text = "FPS must be a whole number from 1 to 240.";
						box.Text = _state.Fps.ToString();
						return;
					}
					if (fps == _state.Fps) return;
					int before = _state.Fps;
					_state.Fps = fps;
					_state.Undo.Push(new SnapshotCommand<int>($"FPS: {fps}", before, fps, v => _state.Fps = v));
					break;
				}
		}
	}
}