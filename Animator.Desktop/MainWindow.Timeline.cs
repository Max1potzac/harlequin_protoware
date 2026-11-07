using Avalonia.Input;

namespace Animator.Desktop;

// The timeline under the viewport: the open animation's keys (TimelinePanel), or at scene level
// the scene's clips (SceneTimelinePanel)
public sealed partial class MainWindow
{
	private bool _sceneTimelineDrag;   // the current press went to the scene timeline
	private bool _timelinePressed;

	private void WireTimeline()
	{
		TimelineView.Draw = (canvas, size, dpi) =>
		{
			canvas.Clear(CanvasTheme.Sk("Canvas"));
			canvas.Scale(dpi);
			if (SceneLevel) _sceneTimeline.Draw(canvas, size);
			else _timeline.Draw(canvas, size);
		};
		TimelineView.PointerPressed += (_, e) =>
		{
			TimelineView.Focus();
			_mods = Input.ModsOf(e.KeyModifiers);
			if (Input.ButtonOf(e.GetCurrentPoint(TimelineView).Properties.PointerUpdateKind) is not { } button) return;
			e.Handled = true;
			TimelineDown(Input.At(e, TimelineView), button, e.ClickCount);
			if (_timelinePressed) e.Pointer.Capture(TimelineView);
		};
		TimelineView.PointerMoved += (_, e) =>
		{
			_mods = Input.ModsOf(e.KeyModifiers);
			TimelineMove(Input.At(e, TimelineView));
		};
		TimelineView.PointerReleased += (_, e) =>
		{
			_mods = Input.ModsOf(e.KeyModifiers);
			TimelineUp();
			e.Pointer.Capture(null);
		};
		TimelineView.PointerWheelChanged += (_, e) =>
		{
			int delta = (int)Math.Round(e.Delta.Y * 120);   // (in the Windows editor's units: 120 a notch)
			if (SceneLevel) _sceneTimeline.Wheel(Input.At(e, TimelineView), delta);
			else _timeline.Wheel(delta);
			e.Handled = true;
		};
	}

	internal void TimelineDown(Vector2 p, PointerButton button, int clicks)
	{
		_sceneTimelineDrag = SceneLevel;
		if (SceneLevel)
		{
			_sceneTimeline.MouseDown(p, button, clicks);
			_timelinePressed = SceneLevel && button == PointerButton.Left;   // not if a clip just opened
			return;
		}
		if (button != PointerButton.Left) return;
		_timeline.MouseDown(p, _mods);
		_timelinePressed = true;
	}

	internal void TimelineMove(Vector2 p)
	{
		if (_sceneTimelineDrag) _sceneTimeline.MouseMove(p);
		else _timeline.MouseMove(p);
	}

	internal void TimelineUp()
	{
		if (!_timelinePressed) return;
		_timelinePressed = false;
		if (_sceneTimelineDrag) _sceneTimeline.MouseUp();
		else _timeline.MouseUp();
		_sceneTimelineDrag = false;
	}
}
