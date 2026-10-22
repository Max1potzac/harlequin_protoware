using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Animator.Core.Editing;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;
using Animator.Viewer.Panels;
using Animator.Viewer.Rendering;
using Animator.Viewer.Tools;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Animator.Viewer;

public partial class MainWindow : Window
{
	private readonly Skeleton _skeleton = new();
	private readonly AssetStore _assets = new();
	private readonly SkeletonRenderer _renderer;
	private readonly Camera _camera = new();
	private readonly EditorState _state;
	private readonly BoneTool _tool;
	private readonly TimelinePanel _timeline;

	// Filled in by the demo code in MainWindow.Demo.cs
	private Animation _anim = new();
	private IkConstraint _ik = null!;

	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private double _last;

	public MainWindow()
	{
		InitializeComponent();
		_renderer = new SkeletonRenderer(_assets);
		BuildDemoRig();
		BuildDemoAnimation();

		_state = new EditorState(_skeleton, _anim);
		_tool = new BoneTool(_state, _camera);
		_timeline = new TimelinePanel(_state);
		_state.Undo.Changed += UpdateStatus;
		InitPaint();

		UpdateStatus();
		CompositionTarget.Rendering += OnFrame;
	}

	// ---------- loop ----------

	private void OnFrame(object? sender, EventArgs e)
	{
		double now = _clock.Elapsed.TotalSeconds;
		float dt = (float)(now - _last);
		_last = now;
		if (_state.Playing) _state.Advance(dt);

		_skeleton.SetToSetupPose();
		_state.Animation.Apply(_skeleton, _state.Time);
		_skeleton.UpdatePose();

		Viewport.InvalidateVisual();
		TimelineView.InvalidateVisual();
		if (PaintPanel.Visibility == Visibility.Visible) PaintView.InvalidateVisual();
	}

	// ---------- painting ----------

	private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
	{
		var canvas = e.Surface.Canvas;
		canvas.Clear(new SKColor(40, 44, 52));
		if (Viewport.ActualWidth <= 0) return;

		canvas.Scale((float)(e.Info.Width / Viewport.ActualWidth));   // DPI fix
		_camera.ViewSize = new((float)Viewport.ActualWidth, (float)Viewport.ActualHeight);
		_camera.Apply(canvas);

		_renderer.Zoom = _camera.Zoom;
		_renderer.Selected = _state.Selected;
		_renderer.Draw(canvas, _skeleton);
	}

	private void OnTimelinePaint(object? sender, SKPaintSurfaceEventArgs e)
	{
		if (TimelineView.ActualWidth <= 0) return;
		var canvas = e.Surface.Canvas;
		canvas.Scale((float)(e.Info.Width / TimelineView.ActualWidth));   // same DPI fix
		_timeline.Draw(canvas, new Vector2((float)TimelineView.ActualWidth, (float)TimelineView.ActualHeight));
	}

	// ---------- viewport mouse ----------

	private static Vector2 PosIn(IInputElement element, MouseEventArgs e)
	{
		var p = e.GetPosition(element);
		return new Vector2((float)p.X, (float)p.Y);
	}

	private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
	{
		Viewport.Focus();
		var pos = PosIn(Viewport, e);
		if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && TryOpenPaintAt(pos)) return;

		_tool.MouseDown(pos, e.ChangedButton);
		Viewport.CaptureMouse();
		UpdateStatus();
	}

	private void Viewport_MouseMove(object sender, MouseEventArgs e) => _tool.MouseMove(PosIn(Viewport, e));

	private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
	{
		_tool.MouseUp();
		Viewport.ReleaseMouseCapture();
	}

	private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e) =>
		_camera.ZoomAt(PosIn(Viewport, e), e.Delta > 0 ? 1.1f : 1 / 1.1f);

	// ---------- timeline mouse ----------

	private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
	{
		TimelineView.Focus();
		var p = PosIn(TimelineView, e);

		if (e.ChangedButton == MouseButton.Right)
		{
			if (_timeline.SelectForMenu(p)) ShowKeyMenu();
			return;
		}
		if (e.ChangedButton != MouseButton.Left) return;

		_timeline.MouseDown(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
		TimelineView.CaptureMouse();
		UpdateStatus();
	}

	private void Timeline_MouseMove(object sender, MouseEventArgs e) => _timeline.MouseMove(PosIn(TimelineView, e));

	private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
	{
		_timeline.MouseUp();
		TimelineView.ReleaseMouseCapture();
	}

	// ---------- keyboard ----------

	private void Window_KeyDown(object sender, KeyEventArgs e)
	{
		bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
		bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
		// Typing in a text box (like the color code) must not trigger shortcuts.
		// Without this, typing "B" in "#FFB000" would flip the elbow instead.
		if (Keyboard.FocusedElement is TextBox) return;

		// Paint shortcuts take priority while the mouse is over the paint canvas
		if (HandlePaintKey(e.Key)) { e.Handled = true; return; }
		// No undo/redo/copy/paste in the middle of a stroke
		if (ctrl && _paintCanvas.IsBusy) { e.Handled = true; return; }

		if (ctrl && e.Key == Key.Z && !shift)
		{
			_state.SelectedKeys.Clear();   // selection may point at keys that won't exist after undo
			_state.Undo.Undo();
		}
		else if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift)))
		{
			_state.SelectedKeys.Clear();
			_state.Undo.Redo();
		}
		else if (ctrl && e.Key == Key.C) _timeline.Copy();
		else if (ctrl && e.Key == Key.V) _timeline.Paste();
		else
		{
			switch (e.Key)
			{
				case Key.Space: _state.Playing = !_state.Playing; break;
				case Key.R: _state.Time = 0; break;
				case Key.K: _state.AnimateMode = !_state.AnimateMode; break;
				case Key.Left: _state.Playing = false; _state.SetFrame(_state.Frame - 1); break;
				case Key.Right: _state.Playing = false; _state.SetFrame(_state.Frame + 1); break;
				case Key.Delete:
					// Selected keys first; otherwise the selected bone's keys at this frame
					if (!_timeline.DeleteSelected()) DeleteKeysAtFrame();
					break;
				case Key.I when shift: SwitchIkHere(); break;
				case Key.I: _ik.SetupMix = _ik.SetupMix > 0.5f ? 0f : 1f; break;
				case Key.T: MatchIk(bake: shift); break;
				case Key.B: _ik.BendDirection = -_ik.BendDirection; break;
				case Key.G: _renderer.ShowImages = !_renderer.ShowImages; break;
				case Key.P: _renderer.PixelArt = !_renderer.PixelArt; break;
				case Key.F: _camera.Reset(); break;
				case Key.Escape: ClosePaintEditor(); break;
				default: return;
			}
		}

		e.Handled = true;
		UpdateStatus();
	}

	// Deletes all keys on the selected bone at the current frame (undoable)
	private void DeleteKeysAtFrame()
	{
		var bone = _state.Selected;
		if (bone == null) return;

		var before = AnimationSnapshot.Capture(_state.Animation);
		bool removed = false;
		foreach (var tl in _state.Animation.Timelines)
			if (tl.Target == bone.Name)
				removed |= tl.RemoveKey(_state.Time);
		if (!removed) return;

		var after = AnimationSnapshot.Capture(_state.Animation);
		_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
			$"Delete key on {bone.Name} @ frame {_state.Frame}", before, after, s => s.Restore()));
	}
	private void ShowKeyMenu()
	{
		var menu = new ContextMenu();
		var current = _timeline.SelectedCurve();

		foreach (var curve in Enum.GetValues<CurveType>())
		{
			var item = new MenuItem
			{
				Header = curve.ToString(),
				IsCheckable = true,
				IsChecked = current == curve   // tick mark shows the current curve
			};
			item.Click += (_, _) => _timeline.SetCurve(curve);
			menu.Items.Add(item);
		}

		menu.Items.Add(new Separator());

		var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
		copy.Click += (_, _) => _timeline.Copy();
		menu.Items.Add(copy);

		var delete = new MenuItem { Header = "Delete", InputGestureText = "Del" };
		delete.Click += (_, _) => _timeline.DeleteSelected();
		menu.Items.Add(delete);

		menu.IsOpen = true;   // opens at the mouse position by default
	}
	// The constraint the selected bone belongs to (as target or chain), else the first one
	private IkConstraint? ActiveIk()
	{
		var sel = _state.Selected;
		return _skeleton.IkConstraints.Find(c => sel != null && (c.Target == sel || c.Bones.Contains(sel)))
			?? _skeleton.IkConstraints.FirstOrDefault();
	}

	// Undoable edit that may touch any bone's rest pose and the animation
	private void EditRig(string name, Action change)
	{
		var before = RigSnapshot.Capture(_skeleton, _state.Animation);
		change();
		var after = RigSnapshot.Capture(_skeleton, _state.Animation);
		_state.Undo.Push(new SnapshotCommand<RigSnapshot>(name, before, after, s => s.Restore()));
	}

	private void MatchIk(bool bake)
	{
		var c = ActiveIk();
		if (c == null) return;

		_state.Playing = false;
		_state.SetFrame(_state.Frame);          // make sure we're exactly on a frame
		bool key = _state.AnimateMode;
		float time = _state.Time;

		if (bake)
			EditRig($"Bake {c.Name} IK into FK",
				() => IkMatching.BakeIkToFk(_skeleton, _state.Animation, c, time, key));
		else
			EditRig($"Snap {c.Target.Name} to {c.Bones[^1].Name}",
				() => IkMatching.SnapTargetToTip(_skeleton, _state.Animation, c, time, key));
	}

	private void SwitchIkHere()
	{
		var c = ActiveIk();
		if (c == null || !_state.AnimateMode) return;   // a keyed switch only makes sense when animating

		_state.Playing = false;
		_state.SetFrame(_state.Frame);

		// Find out which mode we're currently in at this frame, then switch to the other
		IkMatching.Evaluate(_skeleton, _state.Animation, _state.Time);
		bool toIk = c.Mix < 0.5f;
		int frame = _state.Frame;

		EditRig($"Switch {c.Name} to {(toIk ? "IK" : "FK")} @ frame {frame}",
			() => IkMatching.SwitchMode(_skeleton, _state.Animation, c, frame, _state.Fps, toIk));
	}

	private void UpdateStatus() =>
		Status.Text =
			$"{(_state.AnimateMode ? "● ANIMATE: drags set keys" : "SETUP: drags edit the rest pose")}  (K to switch)    " +
			$"Selected: {_state.Selected?.Name ?? "none"}    Last edit: {_state.Undo.NextUndoName ?? "-"}\n" +
			"Viewport: drag joint = move, drag bone = rotate, drag empty / middle = pan, wheel = zoom, F = reset view\n" +
			"Timeline: click key = select, Shift+click = add, drag = move, right-click = curve, Ctrl+C / Ctrl+V = copy / paste at playhead\n" +
			"IK: T = snap target to hand, Shift+T = bake IK into FK, Shift+I = switch IK/FK at this frame (Animate mode)\n" +
			"Paint: double-click a part. Over the canvas: M select, V move, N pencil, B brush, X mixer, E eraser, I picker, G fill, D gradient, U figure, S swap colors, [ ] size, Shift+click / Shift+drag straight line, Ctrl+A / Ctrl+D select all / none, Alt+click pick. Right-click palette = secondary color\n" + 
			"Space: play/pause   R: restart   ←/→: step   Del: delete   Ctrl+Z / Ctrl+Y: undo/redo   " +
			$"I: IK/FK ({(_ik.SetupMix > 0.5f ? "IK" : "FK")})   B: elbow   G: images   P: pixel filtering ({(_renderer.PixelArt ? "on" : "off")})";
}