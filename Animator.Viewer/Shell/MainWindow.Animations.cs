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
		_state.SelectedKeys.Clear();   // selected keys belong to the old animation
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