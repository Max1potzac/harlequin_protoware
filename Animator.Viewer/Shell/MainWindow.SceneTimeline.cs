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
	private int ToFrames(float seconds) => _session.ToFrames(seconds);
	private float ToSeconds(int frames) => _session.ToSeconds(frames);

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

		if (length) SetSceneLength(v);
		else if (field == "SWidth") _session.SetStage(v, _scene.StageHeight);
		else _session.SetStage(_scene.StageWidth, v);
	}

	private void SceneLoop_Click(object sender, RoutedEventArgs e) => _session.SetSceneLoop(SceneLoopBox.IsChecked == true);

	private void FitSceneToClips_Click(object sender, RoutedEventArgs e)
	{
		if (SceneOps.LastClipEnd(_scene) <= 0)
		{
			AnimHint.Text = "There are no clips yet: right-click an object's row in the scene timeline to add one.";
			return;
		}
		_session.FitSceneToClips();
	}

	private void SetSceneLength(int frames) => _session.SetSceneLength(frames);

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
		SetField(ClipMixIn, ToFrames(clip.MixIn).ToString());
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

		switch (field)
		{
			case "CStart": _session.SetClipTiming(obj, clip, start: v); break;
			case "CLength": _session.SetClipTiming(obj, clip, length: v); break;
			case "COffset": _session.SetClipTiming(obj, clip, offset: v); break;
			case "CMixIn": _session.SetClipTiming(obj, clip, mixIn: v); break;
		}
		if (field is "CStart" or "CLength" && ToFrames(field == "CStart" ? clip.Start : clip.Length) != Math.Max(field == "CLength" ? 1 : 0, v))
			ClipHint.Text = "It only goes as far as the clips next to it allow.";
	}

	// The fade in the object's mix plan, from the clip before's animation to this one's
	private void ClipMixFromPlan_Click(object sender, RoutedEventArgs e)
	{
		if (_syncingScene || _objectTool.Selected is not { } obj || _shownClip is not { } clip) return;
		if (obj.ClipBefore(clip) is not { } before)
		{
			ClipHint.Text = "No clip before it to fade in from.";
			return;
		}
		_session.MixClipFromPlan(obj, clip);
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
		if (_session.AddClip(obj, animation, frame) == null)
			AnimHint.Text = "No room there: the next clip starts right away.";
	}

	private void SetClipAnimation(SceneClip clip, Animation animation) => _session.SetClipAnimation(clip, animation);
	private void SetClipLoop(SceneClip clip, bool loop) => _session.SetClipLoop(clip, loop);

	private void DeleteSelectedClip()
	{
		if (_objectTool.Selected is { } obj && _sceneState.SelectedClip is { } clip) _session.DeleteClip(obj, clip);
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
			fit.Click += (_, _) => _session.FitClip(obj, clip);
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
