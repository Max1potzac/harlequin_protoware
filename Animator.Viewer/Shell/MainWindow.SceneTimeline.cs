using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Animator.Viewer;

// The scene timeline's side of the main window: the scene settings bar, the clip panel, the
// right-click menu, and the edits behind them (each one an undo step). The timeline itself is
// drawn and dragged by SceneTimelinePanel.
public partial class MainWindow
{
	private int ToFrames(float seconds) => (int)MathF.Round(seconds * _state.Fps);
	private float ToSeconds(int frames) => frames / (float)Math.Max(1, _state.Fps);

	// ======================= scene settings =======================

	private void ShowSceneSettings()
	{
		SetField(SceneLength, ToFrames(_scene.Duration).ToString());
		SceneLoopBox.IsChecked = _scene.Loop;
		SetField(StageWidthBox, _scene.StageWidth.ToString());
		SetField(StageHeightBox, _scene.StageHeight.ToString());
	}

	private void SceneField_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitSceneField((TextBox)sender);
		TimelineView.Focus();
	}

	private void SceneField_LostFocus(object sender, RoutedEventArgs e) => CommitSceneField((TextBox)sender);

	private void CommitSceneField(TextBox box)
	{
		AnimHint.Text = "";
		string field = (string)box.Tag;
		bool length = field == "SLength";
		if (!int.TryParse(box.Text.Trim(), out int v) || v < (length ? 1 : 0) || v > 100000)
		{
			AnimHint.Text = length ? "The scene's length is a whole number of frames (1 or more)."
								   : "The stage size is a whole number of pixels (0 = no stage).";
			box.Text = length ? ToFrames(_scene.Duration).ToString() : (field == "SWidth" ? _scene.StageWidth : _scene.StageHeight).ToString();
			return;
		}

		EditScene(() =>
		{
			switch (field)
			{
				case "SLength" when v != ToFrames(_scene.Duration):
					_scene.Duration = ToSeconds(v);
					return $"Scene length: {v} frames";
				case "SWidth" when v != _scene.StageWidth:
					_scene.StageWidth = v;
					return $"Stage: {v} × {_scene.StageHeight}";
				case "SHeight" when v != _scene.StageHeight:
					_scene.StageHeight = v;
					return $"Stage: {_scene.StageWidth} × {v}";
				default:
					return null;
			}
		});
		_sceneState.Time = Math.Min(_sceneState.Time, _scene.Duration);   // the playhead stays inside
	}

	private void SceneLoop_Click(object sender, RoutedEventArgs e)
	{
		bool loop = SceneLoopBox.IsChecked == true;
		EditScene(() =>
		{
			if (_scene.Loop == loop) return null;
			_scene.Loop = loop;
			return loop ? "Scene loops" : "Scene plays once";
		});
	}

	private void FitSceneToClips_Click(object sender, RoutedEventArgs e)
	{
		float end = SceneOps.LastClipEnd(_scene);
		if (end <= 0)
		{
			AnimHint.Text = "There are no clips yet: right-click an object's row in the scene timeline to add one.";
			return;
		}
		SetSceneLength(ToFrames(end));
	}

	private void SetSceneLength(int frames)
	{
		frames = Math.Max(1, frames);
		EditScene(() =>
		{
			if (frames == ToFrames(_scene.Duration)) return null;
			_scene.Duration = ToSeconds(frames);
			return $"Scene length: {frames} frames";
		});
		_sceneState.Time = Math.Min(_sceneState.Time, _scene.Duration);
	}

	// ======================= the selected clip =======================

	private void ShowClipProperties()
	{
		_shownClip = _sceneState.SelectedClip;
		var obj = _objectTool.Selected;
		bool show = _shownClip != null && obj != null && obj.Clips.Contains(_shownClip);
		ClipFields.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		ClipHint.Text = "";
		if (!show) return;

		var clip = _shownClip!;
		_syncingScene = true;
		ClipTitle.Text = $"Clip of {obj!.Name} on the scene timeline";
		ClipAnimation.ItemsSource = obj.Animations.ToList();
		ClipAnimation.SelectedItem = obj.Animations.Contains(clip.Animation) ? clip.Animation : null;
		ClipLoop.IsChecked = clip.Loop;
		_syncingScene = false;
		RefreshClipValues();
	}

	// Every frame: drags in the timeline show live
	private void RefreshClipValues()
	{
		if (_shownClip is not { } clip || ClipFields.Visibility != Visibility.Visible) return;
		SetField(ClipStart, ToFrames(clip.Start).ToString());
		SetField(ClipLength, ToFrames(clip.Length).ToString());
		SetField(ClipOffset, ToFrames(clip.Offset).ToString());
	}

	private void ClipField_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		CommitClipField((TextBox)sender);
		TimelineView.Focus();
	}

	private void ClipField_LostFocus(object sender, RoutedEventArgs e) => CommitClipField((TextBox)sender);

	private void CommitClipField(TextBox box)
	{
		if (_syncingScene || _objectTool.Selected is not { } obj || _shownClip is not { } clip) return;
		ClipHint.Text = "";
		string field = (string)box.Tag;
		if (!int.TryParse(box.Text.Trim(), out int v) || v < 0)
		{
			ClipHint.Text = $"\"{box.Text}\" isn't a whole number of frames.";
			box.Text = "";   // the next refresh puts the real value back
			return;
		}

		EditScene(() =>
		{
			var before = (clip.Start, clip.Length, clip.Offset);
			switch (field)
			{
				case "CStart":
					SceneOps.MoveClip(obj, clip, ToSeconds(v));
					break;
				case "CLength":
					SceneOps.SetClipEnd(obj, clip, clip.Start + ToSeconds(Math.Max(1, v)), FrameTime);
					break;
				case "COffset":
					clip.Offset = Math.Clamp(ToSeconds(v), 0f, MathF.Max(0f, clip.Animation.Duration - FrameTime));
					break;
				default:
					return null;
			}
			return (clip.Start, clip.Length, clip.Offset) == before ? null : $"Clip {clip.Animation.Name} of {obj.Name}: {field[1..]}";
		});
		if (field != "COffset" && ToFrames(field == "CStart" ? clip.Start : clip.Length) != v)
			ClipHint.Text = "It only goes as far as the clips next to it allow.";
	}

	private void ClipAnimation_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingScene || _shownClip is not { } clip || ClipAnimation.SelectedItem is not Animation animation) return;
		SetClipAnimation(clip, animation);
	}

	private void ClipLoop_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingScene || _shownClip is not { } clip) return;
		SetClipLoop(clip, ClipLoop.IsChecked == true);
	}

	private void ClipEdit_Click(object sender, RoutedEventArgs e)
	{
		if (_objectTool.Selected is { } obj && _shownClip is { } clip) EnterObject(obj, clip);
	}

	private void ClipDelete_Click(object sender, RoutedEventArgs e) => DeleteSelectedClip();

	// ======================= clip edits =======================

	// A clip of `animation` on the object's track at a scene frame, one full play long
	private void AddClip(SceneObject obj, Animation animation, int frame)
	{
		SceneClip? added = null;
		bool done = EditScene(() =>
		{
			added = SceneOps.AddClip(obj, animation, ToSeconds(frame), FrameTime);
			return added != null ? $"Add clip {animation.Name} to {obj.Name}" : null;
		});
		if (!done)
		{
			AnimHint.Text = "No room there: the next clip starts right away.";
			return;
		}
		_objectTool.Selected = obj;
		_sceneState.SelectedClip = added;
	}

	private void SetClipAnimation(SceneClip clip, Animation animation)
	{
		EditScene(() =>
		{
			if (clip.Animation == animation) return null;
			clip.Animation = animation;
			clip.Offset = Math.Min(clip.Offset, MathF.Max(0f, animation.Duration - FrameTime));
			return $"Clip plays {animation.Name}";
		});
	}

	private void SetClipLoop(SceneClip clip, bool loop)
	{
		EditScene(() =>
		{
			if (clip.Loop == loop) return null;
			clip.Loop = loop;
			return loop ? $"Clip {clip.Animation.Name} loops" : $"Clip {clip.Animation.Name} holds its last frame";
		});
	}

	private void DeleteSelectedClip()
	{
		if (_objectTool.Selected is not { } obj || _sceneState.SelectedClip is not { } clip) return;
		EditScene(() => obj.Clips.Remove(clip) ? $"Delete clip {clip.Animation.Name} of {obj.Name}" : null);
		_sceneState.SelectedClip = null;
	}

	private void AddClipAtPlayhead_Click(object sender, RoutedEventArgs e)
	{
		if (_objectTool.Selected is not { } obj) return;
		var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
		int frame = ToFrames(_sceneState.Time);
		foreach (var animation in obj.Animations)
		{
			var item = new MenuItem { Header = animation.Name };
			item.Click += (_, _) => AddClip(obj, animation, frame);
			menu.Items.Add(item);
		}
		menu.IsOpen = true;
	}

	// Right-click in the scene timeline: on a clip, its options; anywhere on a row, adding a clip
	private void ShowClipMenu(SceneObject obj, SceneClip? clip, int frame)
	{
		var menu = new ContextMenu { PlacementTarget = TimelineView, Placement = PlacementMode.MousePoint };

		if (clip != null)
		{
			var animations = new MenuItem { Header = "Animation" };
			foreach (var animation in obj.Animations)
			{
				var item = new MenuItem { Header = animation.Name, IsCheckable = true, IsChecked = animation == clip.Animation };
				item.Click += (_, _) => SetClipAnimation(clip, animation);
				animations.Items.Add(item);
			}
			menu.Items.Add(animations);

			var loop = new MenuItem { Header = "Loop", IsCheckable = true, IsChecked = clip.Loop,
									  ToolTip = "Off: after its last frame, the animation holds it" };
			loop.Click += (_, _) => SetClipLoop(clip, !clip.Loop);
			menu.Items.Add(loop);

			var fit = new MenuItem { Header = "One play long" };
			fit.Click += (_, _) => EditScene(() => SceneOps.FitClip(obj, clip, FrameTime) ? $"Clip {clip.Animation.Name}: one play long" : null);
			menu.Items.Add(fit);

			var edit = new MenuItem { Header = "Edit in place ▸" };
			edit.Click += (_, _) => EnterObject(obj, clip);
			menu.Items.Add(edit);

			var delete = new MenuItem { Header = "Delete clip", InputGestureText = _keys.Gesture("edit.delete") };
			delete.Click += (_, _) => DeleteSelectedClip();
			menu.Items.Add(delete);
			menu.Items.Add(new Separator());
		}

		var add = new MenuItem { Header = $"Add clip at frame {frame}" };
		foreach (var animation in obj.Animations)
		{
			var item = new MenuItem { Header = animation.Name };
			item.Click += (_, _) => AddClip(obj, animation, frame);
			add.Items.Add(item);
		}
		menu.Items.Add(add);

		if (frame > 0)
		{
			var end = new MenuItem { Header = $"End the scene at frame {frame}" };
			end.Click += (_, _) => SetSceneLength(frame);
			menu.Items.Add(end);
		}

		menu.IsOpen = true;
	}
}
