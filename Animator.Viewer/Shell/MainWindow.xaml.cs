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
		_scene = SingleObjectScene();
		_tool = new BoneTool(_state, _camera, _renderer);
		_timeline = new TimelinePanel(_state);
		_state.Undo.Changed += UpdateStatus;
		InitPaint();
		InitRigPanel();
		InitAnimationsPanel();
		InitOnion();
		InitEffects();

		UpdateStatus();
		CompositionTarget.Rendering += OnFrame;
	}

	// ---------- loop ----------

	private void OnFrame(object? sender, EventArgs e)
	{
		double now = _clock.Elapsed.TotalSeconds;
		float dt = (float)(now - _last);
		_last = now;
		if (_state.Playing)
		{
			float before = _state.Time;
			_state.Advance(dt);
			FireEvents(before, _state.Time);
		}
		UpdateEventFlash();

		PoseAt(_state.Time);

		Viewport.InvalidateVisual();
		TimelineView.InvalidateVisual();
		SyncRigSelection();
		if (PaintPanel.Visibility == Visibility.Visible)
		{
			FollowPaintDrawing();
			PaintView.InvalidateVisual();
		}
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
		_renderer.SelectedSlot = _state.SelectedSlot;
		_renderer.ShowPivotHandle = !_state.AnimateMode;   // in Animate mode, part drags set placement keys
		_renderer.GhostBone = _tool.CreatePreview;
		DrawOnionGhosts(canvas);   // behind everything else
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
			else if (_timeline.IsInKeyArea(p)) ShowTimelineMenu(_timeline.FrameAt(p.X));
			return;
		}
		if (e.ChangedButton != MouseButton.Left) return;

		// Double-click an event marker to rename it or change its data
		if (e.ClickCount == 2 && _timeline.EventAt(p) is { } clicked)
		{
			EditEventAt(clicked.Events, clicked.Frame);
			return;
		}

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

	private void Timeline_MouseWheel(object sender, MouseWheelEventArgs e) => _timeline.Wheel(e.Delta);

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
		if (ctrl && e.Key == Key.S) { if (shift) SaveProjectAs(); else SaveProject(); e.Handled = true; return; }
		if (ctrl && e.Key == Key.O) { OpenProject(); e.Handled = true; return; }
		if (ctrl && e.Key == Key.N) { NewProject(); e.Handled = true; return; }

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
				case Key.I: ToggleIkMode(); break;
				case Key.T: MatchIk(bake: shift); break;
				case Key.B: FlipElbow(); break;
				case Key.G: _renderer.ShowImages = !_renderer.ShowImages; break;
				case Key.P: _renderer.PixelArt = !_renderer.PixelArt; break;
				case Key.F: _camera.Reset(); break;
				case Key.E: AddEventAt(_state.Frame); break;
				case Key.F5 when shift: ChangeHold(-1); break;
				case Key.F5: ChangeHold(+1); break;
				case Key.F6: NewDrawing(copy: true); break;
				case Key.F7: NewDrawing(copy: false); break;
				case Key.O: ToggleOnion(); break;
				case Key.Escape: ClosePaintEditor(); break;
				default: return;
			}
		}

		e.Handled = true;
		UpdateStatus();
	}

	// Deletes the selected part's keys at the current frame, or else the selected bone's (undoable)
	private void DeleteKeysAtFrame()
	{
		var slot = _state.SelectedSlot;
		string? target = slot?.Name ?? _state.Selected?.Name;
		if (target == null) return;

		// A part and a bone can share a name, so the kind of row matters too
		Func<Timeline, bool> mine = slot != null ? PartOps.TargetsPart : RigOps.TargetsBone;

		var before = AnimationSnapshot.Capture(_state.Animation);
		bool removed = false;
		foreach (var tl in _state.Animation.Timelines)
			if (tl.Target == target && mine(tl))
				removed |= tl.RemoveKey(_state.Time);
		if (!removed) return;

		var after = AnimationSnapshot.Capture(_state.Animation);
		_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
			$"Delete key on {target} @ frame {_state.Frame}", before, after, s => s.Restore()));
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
	private void ToggleIkMode()
	{
		if (ActiveIk() is not { } ik) return;
		float before = ik.SetupMix;
		float after = before > 0.5f ? 0f : 1f;
		ik.SetupMix = after;
		_state.Undo.Push(new SnapshotCommand<float>($"{ik.Name}: {(after > 0.5f ? "IK" : "FK")}",
			before, after, v => ik.SetupMix = v));
	}

	private void FlipElbow()
	{
		if (ActiveIk() is not { } ik) return;
		int before = ik.BendDirection;
		ik.BendDirection = -before;
		_state.Undo.Push(new SnapshotCommand<int>($"Flip {ik.Name} bend",
			before, -before, v => ik.BendDirection = v));
	}

	private void UpdateStatus()
	{
		bool ikOn = (ActiveIk()?.SetupMix ?? 0f) > 0.5f;

		Status.Text =
			$"{(_state.AnimateMode ? "● ANIMATE: drags set keys" : "SETUP: drags edit the rest pose")}  (K to switch)    " +
			$"Selected: {_state.Selected?.Name ?? "none"}    Last edit: {_state.Undo.NextUndoName ?? "-"}\n" +
			"Viewport: drag joint = move, drag bone = rotate, click a part = select (drag box / corners / round handle / pivot; in Animate mode, part drags set placement keys), Ctrl+drag = new bone, drag empty / middle = pan, wheel = zoom, F = reset view\n" +
			"Timeline: click key = select, Shift+click = add, drag = move, right-click = curve, Ctrl+C / Ctrl+V = copy / paste at playhead\n" +
			"IK: T = snap target to hand, Shift+T = bake IK into FK, Shift+I = switch IK/FK at this frame (Animate mode)\n" +
			"Detach / attach: \"Follows\" in the bone panel (Animate mode), Throw / fall below it.   Events: E = add at playhead, right-click timeline = add here, double-click a flag = edit\n" +
			"Frame by frame (select a part): F7 = new drawing, F6 = copy drawing, F5 / Shift+F5 = hold longer / shorter, Del = its key here.   O = onion skin\n" +
			"Paint: double-click a part. Over the canvas: M select, W wand, V move, N pencil, B brush, X mixer, E eraser, I picker, G fill, D gradient, U figure, S swap colors, [ ] size, Shift+click / Shift+drag line, Alt+click pick, Ctrl+D deselect, Ctrl+Shift+I invert\n" +
			"Space: play/pause   R: restart   ←/→: step   Del: delete   Ctrl+Z / Ctrl+Y: undo/redo   " +
			$"I: IK/FK ({(ikOn ? "IK" : "FK")})   B: elbow   G: images   P: pixel filtering ({(_renderer.PixelArt ? "on" : "off")})\n" +
			"File: Ctrl+N new   Ctrl+O open   Ctrl+S save   Ctrl+Shift+S save as";

		UpdateTitle();
	}
}