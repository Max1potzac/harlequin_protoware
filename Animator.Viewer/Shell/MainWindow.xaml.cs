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

public partial class MainWindow : Window, IScalesItsParts
{
	private Skeleton _skeleton = new();   // the rig of the object being edited (see MainWindow.Scene.cs)
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
		Themes.Apply(Themes.Find(_appSettings.Theme));   // (before the window's parts take its colours)
		InitializeComponent();
		RestoreLayout();   // the window and its panels as they were left
		UiScale.Apply(_appSettings.UiScale);
		Themes.Changed += ThemeChanged;
		Closed += (_, _) => Themes.Changed -= ThemeChanged;
		_renderer = new SkeletonRenderer(_assets);
		BuildDemoRig();
		BuildDemoAnimation();

		_state = new EditorState(_skeleton, _anim);
		_scene = SingleObjectScene();
		EditObject(_scene.Objects[0]);
		InitScene();   // before the other panels: an undo can change which object is open
		_tool = new BoneTool(_state, _camera, _renderer);
		_timeline = new TimelinePanel(_state);
		_state.Undo.Changed += UpdateStatus;
		InitPaint();
		InitRigPanel();
		InitAnimationsPanel();
		InitOnion();
		InitEffects();
		InitKeys();   // the shortcuts and mouse buttons from Settings
		InitPlaces();   // undo first goes back to where an edit was made
		InitViewAids();   // rulers, grid, guides and snapping
		InitAutosave();
		InitTimelineTools();   // auto key, the rows shown, playback
		InitScrub();   // number fields that can be dragged

		UpdateStatus();
		CompositionTarget.Rendering += OnFrame;
	}

	// ---------- loop ----------

	private void OnFrame(object? sender, EventArgs e)
	{
		double now = _clock.Elapsed.TotalSeconds;
		float dt = (float)(now - _last);
		_last = now;
		if (_exporting) return;   // the export poses the scene itself, frame by frame
		if (_state.Playing && _sceneLevel)
		{
			AdvanceScene(dt * _state.Speed);
		}
		else if (_state.Playing)
		{
			_state.PlayRange = _appSettings.LoopPickedFrames ? _timeline.Range : null;
			FireEvents(_state.Advance(dt));
		}
		AdvanceMixer(dt);
		SyncSound();
		// Unkeyed changes (auto key off) last as long as the playhead stays on their frame
		_state.Hold.KeepOnlyAt(_skeleton, _state.Animation, _state.Frame, Editing && _state.AnimateMode && !_state.Playing);
		UpdateEventFlash();

		PoseScene(ClockTime);
		UpdateCameraSpace();   // an object attached to a bone moves with it

		Viewport.InvalidateVisual();
		TimelineView.InvalidateVisual();
		SyncRigSelection();
		SyncLayerOrder();
		SyncColourPanels();
		SyncViewToolBar();
		SyncWorkspace();
		SyncScenePanel();
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
		canvas.Clear(Themes.Sk("Viewport"));
		if (Viewport.ActualWidth <= 0) return;

		float dpi = (float)(e.Info.Width / Viewport.ActualWidth);
		canvas.Scale(dpi);   // DPI fix
		_camera.ViewSize = new((float)Viewport.ActualWidth, (float)Viewport.ActualHeight);
		_camera.Apply(canvas);   // scene space from here on
		_snap.GridSize = _scene.GridSize;   // (the project's: it changes with undo, or another file)
		_snap.Guides = _scene.Guides;

		_renderer.Zoom = _camera.Zoom;
		_renderer.Selected = _state.Selected;
		_renderer.SelectedSlot = _state.SelectedSlot;
		_renderer.HideHidden = !_sceneLevel;   // bones and parts hidden with their eyes: while editing
		_renderer.SkipLocked = !_sceneLevel;   // ...and locked ones can't be picked
		_renderer.PickedBones = _sceneLevel ? NoBones : _state.PickedBones.ToHashSet();
		_renderer.PickedSlots = _sceneLevel ? NoSlots : _state.PickedSlots.ToHashSet();
		_renderer.SelectionBox = _sceneLevel ? null : _tool.SelectionBox;
		_renderer.ShowPivotHandle = !_state.AnimateMode && _state.Picked.Count <= 1;   // in Animate mode, part drags set placement keys
		_renderer.ShowBones = !IsDrawn;                    // a drawn object's one bone stays hidden
		_renderer.PartsOf = !_sceneLevel && !IsDrawn && _state.SelectedSlot == null ? _state.Selected : null;   // what's on the selected bone
		_renderer.GhostBone = _tool.CreatePreview;
		_renderer.TipHandleOf = !_sceneLevel && !IsDrawn ? _tool.TipHandle : null;   // Setup mode: where the selected bone ends
		DrawScene(canvas);   // onion ghosts, every object, then the open object's bones and handles
		DrawGuides(canvas);

		canvas.ResetMatrix();
		canvas.Scale(dpi);   // screen units from here on
		DrawRulers(canvas);
	}

	private static readonly HashSet<Bone> NoBones = new();
	private static readonly HashSet<Slot> NoSlots = new();

	private void OnTimelinePaint(object? sender, SKPaintSurfaceEventArgs e)
	{
		if (TimelineView.ActualWidth <= 0) return;
		var canvas = e.Surface.Canvas;
		canvas.Scale((float)(e.Info.Width / TimelineView.ActualWidth));   // same DPI fix
		var size = new Vector2((float)TimelineView.ActualWidth, (float)TimelineView.ActualHeight);
		if (_sceneLevel) _sceneTimeline.Draw(canvas, size);   // at scene level: the scene timeline
		else if (_graphMode) Graph.Draw(canvas, size);        // the curves
		else _timeline.Draw(canvas, size);
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

		// The rulers and guides come first: out of a ruler comes a guide, a guide moves
		if (_guideDrag == null && GuideMouseDown(pos, e.ChangedButton))
		{
			Viewport.CaptureMouse();
			return;
		}

		// Scene level: whole objects
		_objectDrag = _sceneLevel;
		if (_sceneLevel)
		{
			_objectTool.MouseDown(pos, e.ChangedButton, e.ClickCount);
			if (_sceneLevel) Viewport.CaptureMouse();   // not when a double-click just opened an object
			UpdateStatus();
			return;
		}

		// Inside an object: double-click a part to paint it, or another object to open that one
		// (with the Paint tool, one click does)
		if (e.ChangedButton == _tool.SelectButton && (e.ClickCount == 2 || _tool.Tool == ViewTool.Paint) &&
			(TryOpenPaintAt(pos) || TryEnterOtherObjectAt(pos))) return;

		// The mesh tools (the part panel's Mesh box): its points, or its weights
		if (MeshMouseDown(pos, e.ChangedButton))
		{
			Viewport.CaptureMouse();
			return;
		}

		// The Boxes view open: a hitbox or point under the mouse is picked and dragged
		if (BoxMouseDown(pos, e.ChangedButton))
		{
			Viewport.CaptureMouse();
			return;
		}

		_tool.MouseDown(pos, e.ChangedButton);
		Viewport.CaptureMouse();
		UpdateStatus();
	}

	private void Viewport_MouseMove(object sender, MouseEventArgs e)
	{
		var pos = PosIn(Viewport, e);
		_mouseOnView = pos;
		if (_guideDrag != null)
		{
			GuideMouseMove(pos);
			return;
		}
		if (_boxDrag != null)
		{
			BoxMouseMove(pos);
			return;
		}
		if (_meshDrag != null)
		{
			MeshMouseMove(pos);
			return;
		}
		if (!Viewport.IsMouseCaptured)
		{
			ShowViewPointer(pos);
			BoxHoverAt(pos);
			MeshMouseMove(pos);   // (which point the mouse is over)
		}
		if (_objectDrag || _sceneLevel) _objectTool.MouseMove(pos);
		else _tool.MouseMove(pos);
	}

	private void Viewport_MouseLeave(object sender, MouseEventArgs e) => _mouseOnView = null;

	private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (_guideDrag != null)
		{
			GuideMouseUp(PosIn(Viewport, e));
			Viewport.ReleaseMouseCapture();
			return;
		}
		if (_boxDrag != null)
		{
			BoxMouseUp();
			Viewport.ReleaseMouseCapture();
			return;
		}
		if (_meshDrag != null)
		{
			MeshMouseUp();
			Viewport.ReleaseMouseCapture();
			return;
		}
		if (_objectDrag) _objectTool.MouseUp();
		else _tool.MouseUp();
		_objectDrag = false;
		Viewport.ReleaseMouseCapture();
	}

	private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e) =>
		_camera.ZoomAt(PosIn(Viewport, e), e.Delta > 0 ? 1.1f : 1 / 1.1f);

	// ---------- timeline mouse ----------

	private bool _sceneTimelineDrag;   // the current timeline press went to the scene timeline

	private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
	{
		TimelineView.Focus();
		var p = PosIn(TimelineView, e);

		_sceneTimelineDrag = _sceneLevel;
		if (_sceneLevel)
		{
			_sceneTimeline.MouseDown(p, e.ChangedButton, e.ClickCount);
			if (_sceneLevel && e.ChangedButton == MouseButton.Left) TimelineView.CaptureMouse();   // not if a clip just opened
			UpdateStatus();
			return;
		}

		if (_graphMode)
		{
			// The curves: a key's menu on a right-click, the rest is the graph's
			if (e.ChangedButton == MouseButton.Right)
			{
				if (Graph.SelectForMenu(p)) ShowKeyMenu();
				return;
			}
			Graph.MouseDown(p, Keyboard.Modifiers, e.ChangedButton, e.ClickCount);
			TimelineView.CaptureMouse();
			UpdateStatus();
			return;
		}

		if (e.ChangedButton == MouseButton.Right)
		{
			// Inside the picked frames, their menu comes first
			int frame = _timeline.FrameAt(p.X);
			(int, int)? picked = _timeline.Range is { } range && frame >= range.First && frame <= range.Last ? range : null;
			if (_timeline.SelectForMenu(p)) ShowKeyMenu(picked);
			else if (_timeline.IsInKeyArea(p) || picked != null) ShowTimelineMenu(frame, picked);
			return;
		}
		if (e.ChangedButton != MouseButton.Left) return;

		// Double-click an event marker to rename it or change its data
		if (e.ClickCount == 2 && _timeline.EventAt(p) is { } clicked)
		{
			EditEventAt(clicked.Events, clicked.Frame);
			return;
		}

		_timeline.MouseDown(p, Keyboard.Modifiers);
		TimelineView.CaptureMouse();
		UpdateStatus();
	}

	private void Timeline_MouseMove(object sender, MouseEventArgs e)
	{
		var p = PosIn(TimelineView, e);
		if (_sceneTimelineDrag) _sceneTimeline.MouseMove(p);
		else if (_graphMode) Graph.MouseMove(p, Keyboard.Modifiers);
		else _timeline.MouseMove(p);
	}

	private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (_sceneTimelineDrag) _sceneTimeline.MouseUp();
		else if (_graphMode) Graph.MouseUp();
		else _timeline.MouseUp();
		_sceneTimelineDrag = false;
		TimelineView.ReleaseMouseCapture();
	}

	private void Timeline_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (_sceneLevel) _sceneTimeline.Wheel(PosIn(TimelineView, e), e.Delta);
		else if (_graphMode) Graph.Wheel(PosIn(TimelineView, e), e.Delta);
		else _timeline.Wheel(e.Delta);
	}

	// Deletes the keys at the current frame of the picked parts and bones (a picked part: its own
	// keys, not its bone's). Undoable.
	private void DeleteKeysAtFrame()
	{
		var picked = _state.Picked;
		if (picked.Count == 0) return;

		// A part and a bone can share a name, so the kind of row matters too
		var bones = picked.OfType<Bone>().Select(b => b.Name).ToHashSet();
		var parts = picked.OfType<Slot>().Select(s => s.Name).ToHashSet();

		var before = AnimationSnapshot.Capture(_state.Animation);
		bool removed = false;
		foreach (var tl in _state.Animation.Timelines)
			if ((RigOps.TargetsBone(tl) && bones.Contains(tl.Target)) || (PartOps.TargetsPart(tl) && parts.Contains(tl.Target)))
				removed |= tl.RemoveKey(_state.Time);
		if (!removed) return;

		string what = picked.Count > 1 ? $"{picked.Count} picked" : picked[0] is Slot slot ? slot.Name : ((Bone)picked[0]).Name;
		var after = AnimationSnapshot.Capture(_state.Animation);
		_state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
			$"Delete keys of {what} @ frame {_state.Frame}", before, after, s => s.Restore()));
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

	// The mouse buttons, as the status line names them
	private static string ButtonName(MouseButton button) => button switch
	{
		MouseButton.Left => "Left",
		MouseButton.Right => "Right",
		MouseButton.Middle => "Middle",
		MouseButton.XButton1 => "Back-button",
		_ => "Forward-button"
	};

	// The paint keys, for the status line: "M select, W wand, …"
	private string PaintKeys() => string.Join(", ", KeyMap.Commands
		.Where(c => c.Scope == KeyScope.Paint && _keys.Gesture(c.Id).Length > 0)
		.Select(c => $"{_keys.Gesture(c.Id)} {c.Name.ToLowerInvariant()}"));

	private void UpdateStatus()
	{
		string pick = ButtonName(_tool?.SelectButton ?? MouseButton.Left);
		string move = ButtonName(_tool?.TransformButton ?? MouseButton.Right);
		string pan = ButtonName(_tool?.PanButton ?? MouseButton.Middle);
		string K(string id) => KeyOf(id);
		string pixels = _renderer.PixelArt ? "on" : "off";

		if (_sceneLevel)
		{
			string text =
				$"SCENE    Object: {_objectTool?.Selected?.Name ?? "none"}    Last edit: {_state.Undo.NextUndoName ?? "-"}{UndoHint()}\n" +
				$"Viewport: {pick}-click an object = select, double-click (or {K("scene.open")}) = edit it.   {move}-drag an object = move, a corner = scale (Shift: keep proportions), the round handle = turn (Shift: 15° steps)\n" +
				$"{pan}-drag (or {move} on empty space) = move the view, wheel = zoom, {K("view.reset")} = reset view\n" +
				$"{ViewAidsHelp()}\n" +
				$"Number fields: drag left or right to change them (Shift: faster, Ctrl: finer), click to type.   Keys ▾ (the timeline bar) = what right-clicking the timeline does.   {K("edit.history")} = the history: every step, to go back to any of them\n" +
				"Scene timeline: right-click a row = add a clip, drag a clip = move, drag its edges = length / trim, double-click = edit it in place\n" +
				"Objects without clips loop their preview animation.   Wheel / Shift+wheel / Ctrl+wheel = scroll / scroll in time / zoom\n" +
				$"{K("play.toggle")}: play/pause   {K("play.restart")}: restart   {K("play.back")} / {K("play.on")}: step   {K("edit.delete")}: delete clip (or object)   {K("edit.undo")} / {K("edit.redo")}: undo/redo   " +
				$"{K("view.images")}: images   {K("view.onion")}: onion skin   {K("view.pixels")}: pixel filtering ({pixels})\n" +
				$"File: {K("file.new")} new   {K("file.open")} open   {K("file.save")} save   {K("file.saveAs")} save as   {K("file.import")} import   {K("file.export")} export   {K("file.settings")} settings (keys, mouse, autosave)";
			ShowStatus(text);
			UpdateTitle();
			return;
		}

		bool ikOn = (ActiveIk()?.SetupMix ?? 0f) > 0.5f;
		int count = _state.Picked.Count;
		string shown = _state.SelectedSlot?.Name ?? _state.Selected?.Name ?? "none";
		string picked = count > 1 ? $"{shown} + {count - 1} more" : shown;

		string status =
			$"{(!_state.AnimateMode ? "BUILD: drags edit the rest pose" : _state.AutoKey ? "● ANIMATE: drags set keys, or move the selected keys of what's dragged" : $"● ANIMATE, auto key off: drags pose without keys ({K("anim.key")} keys the pose, another frame drops it)")}  ({K("anim.mode")} to switch, {K("anim.autoKey")}: auto key)    " +
			$"Object: {_active?.Name ?? "-"}    Picked: {picked}    Last edit: {_state.Undo.NextUndoName ?? "-"}{UndoHint()}\n" +
			$"Tool: {_tool?.Tool.ToString() ?? "Select"} ({K("tool.select")} select, {K("tool.move")} move, {K("tool.rotate")} rotate, {K("tool.paint")} paint: the buttons at the viewport's left).   " +
			$"Viewport: {pick}-click = pick (Ctrl: toggle, Shift: add), {pick}-drag = pick in a box (Shift: add, Ctrl: let go), double-click a part = paint it.   " +
			$"{move}-drag: a joint = move, a bone = turn (Shift: 15° steps), a part = move (its corners / round handle / pivot), anything picked = all that's picked; Ctrl+{move}-drag = new bone, Alt+{move}-click a part = put it on the selected bone, the ring at a bone's end = where it ends (Build).   " +
			$"{pan}-drag (or {move} on empty space) = move the view, wheel = zoom, {K("view.reset")} = reset view\n" +
			$"{ViewAidsHelp()}\n" +
				$"Number fields: drag left or right to change them (Shift: faster, Ctrl: finer), click to type.   Keys ▾ (the timeline bar) = what right-clicking the timeline does.   {K("edit.history")} = the history: every step, to go back to any of them\n" +
			$"Rig panel: Ctrl+click = pick several, Shift+click = every row from the selected one, {K("pick.all")} = everything shown, ● = hide / show ({K("pick.hide")}: hide the picked, {K("pick.hideOthers")}: hide the others, {K("pick.showAll")}: show all), " +
			$"padlock = lock / unlock: no clicking or dragging it in the viewport ({K("pick.lock")}: lock the picked, {K("pick.lockOthers")}: lock the others, {K("pick.unlockAll")}: unlock all)\n" +
			"Sets (under the list): + Set = a set of the picked; click a set = pick it again (Ctrl: picked or not, Shift: add), right-click = change it.   The square in the bone panel = the colour of the picked bones.   " +
			"Colour (beside Bones / Order) = colour layers on the whole object (parts can be left out); in the paint editor, + Colour layer = one for that drawing. Keyed in Animate; exports bake them in when asked\n" +
			$"Timeline: click key = select, Shift+click = add, Ctrl+drag = a box of keys, Alt+click = every key at that frame, drag = move, right-click = curve, stretch, reverse, {K("keys.earlier")} / {K("keys.later")} = a frame earlier / later, {K("edit.copy")} / {K("edit.paste")} = copy / paste at playhead, {K("pick.keys")} = every key of the picked, drag across empty frames = pick them (right-click: split into an animation sequence; play loops them)\n" +
			$"Poses (beside Colour): keep a pose by name, put it on (partly, mirrored).   {K("pose.mirror")} = mirror the pose, {K("pose.copy")} / {K("pose.paste")} = copy / paste the pose, {K("pose.pasteMirrored")} = paste it mirrored (sides from names: arm_L / arm_R).   " +
			$"Curves ({K("anim.graph")}): the keys' numbers as curves; click / Shift+click a key, drag it (Shift: one way), drag its square handles to ease it (Bézier), wheel = zoom, middle-drag = move, double-click = fit, click a name = hide it.   " +
			$"Frames: {K("time.insert")} = insert one after the playhead, {K("time.remove")} = remove the one at it, {K("play.prevKey")} / {K("play.nextKey")} = to the key before / after, {K("anim.key")} = key the pose (the unkeyed changes, or the picked)\n" +
			$"IK: {K("ik.snap")} = snap target to hand, {K("ik.bake")} = bake IK into FK, {K("ik.switch")} = switch IK/FK at this frame (Animate).   Detach / attach: \"Follows\" in the bone panel (Animate), Throw / fall below it.   " +
			$"Events: {K("anim.event")} = add at playhead, right-click timeline = add here, double-click a flag = edit\n" +
			$"Frame by frame (select a part): {K("frames.new")} = new drawing, {K("frames.copy")} = copy drawing, {K("frames.longer")} / {K("frames.shorter")} = hold longer / shorter, {K("edit.delete")} = its key here.   {K("view.onion")} = onion skin\n" +
			$"Paint: double-click a part (on another object: edit that object). Over the canvas: {PaintKeys()}; Shift+click / Shift+drag = line, Alt+click = pick a colour\n" +
			$"{K("play.toggle")}: play/pause   {K("play.restart")}: restart   {K("play.back")} / {K("play.on")}: step   {K("edit.delete")}: delete   {K("edit.undo")} / {K("edit.redo")}: undo/redo   {K("edit.back")}: back to the scene   " +
			$"{K("ik.toggle")}: IK/FK ({(ikOn ? "IK" : "FK")})   {K("ik.flip")}: elbow   {K("view.images")}: images   {K("view.pixels")}: pixel filtering ({pixels})\n" +
			$"File: {K("file.new")} new   {K("file.open")} open   {K("file.save")} save   {K("file.saveAs")} save as   {K("file.import")} import   {K("file.export")} export   {K("file.settings")} settings (keys, mouse, autosave)";
		ShowStatus(status);

		UpdateTitle();
	}
}