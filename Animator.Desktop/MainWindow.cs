using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Animator.Desktop;

// The editor window: the menus and the tool bar on top, the rig (or the scene's objects) on the
// left, the viewport in the middle, the timeline under it, a status line at the bottom. What it
// shows and edits is the engine's (EditorSession, the tools, the panels, ViewportPainter); this
// window lays them out, turns Avalonia's mouse and keys into theirs, and runs the frame loop.
public sealed partial class MainWindow : Window
{
	private readonly AssetStore _assets = new();
	private readonly AppSettings _settings = AppSettings.Load();
	private readonly EditorSession _session;
	private readonly Camera _camera = new();
	private readonly SkeletonRenderer _renderer;
	private readonly Snapping _snap = new();
	private readonly BoneTool _tool;
	private readonly ObjectTool _objectTool;
	private readonly GuideTool _guides;
	private readonly MeshEditTool _mesh;
	private readonly ViewportPainter _painter;
	private readonly TimelinePanel _timeline;
	private readonly SceneTimelinePanel _sceneTimeline;

	internal readonly SkiaView Viewport = new();
	internal readonly SkiaView TimelineView = new();
	private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
	private readonly TextBlock _hint = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };

	private KeyMods _mods;   // the modifier keys held, as last seen (the tools ask for them at any time)
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private double _last;
	private readonly DispatcherTimer _frames;

	internal EditorSession Session => _session;
	internal Camera Camera => _camera;
	internal TabControl LeftTabs { get; private set; } = null!;
	internal EditorState State => _session.State;
	private Scene Scene => _session.Scene;
	private bool Editing => _session.Editing;
	private bool SceneLevel => _session.SceneLevel;

	public MainWindow()
	{
		Title = "Harlequin";
		Width = 1280;
		Height = 800;
		ShowThemeColours();
		ActualThemeVariantChanged += (_, _) => ShowThemeColours();

		_renderer = new SkeletonRenderer(_assets);
		var (skeleton, animation) = NewCharacter();
		_session = new EditorSession(_assets, new EditorState(skeleton, animation));
		_session.Replace(CharacterScene(skeleton, animation), null, null, sceneLevel: false);

		_tool = new BoneTool(State, _camera, _renderer) { Modifiers = () => _mods, Snap = _snap };
		_objectTool = new ObjectTool(() => _session.Scene, _session.SceneState, _camera, _renderer, State.Undo) { Modifiers = () => _mods, Snap = _snap };
		_objectTool.EnterRequested += obj => EnterObject(obj);
		_guides = new GuideTool(_session, _camera) { Snap = _snap, SomethingToPickAt = SomethingToPickAt };
		_mesh = new MeshEditTool(_session, _camera);
		_painter = new ViewportPainter(_session, _camera, _renderer, _tool, _objectTool, _guides, _mesh);
		_timeline = new TimelinePanel(State);
		_sceneTimeline = new SceneTimelinePanel(() => _session.Scene, _session.SceneState, State) { Modifiers = () => _mods };
		_sceneTimeline.ClipOpened += (obj, clip) => EnterObject(obj, clip);
		_keys = KeyMap.From(_settings.Shortcuts);
		ApplySettings();
		ApplyAppearance();

		Content = BuildLayout();
		ShowPaintToolKeys();
		InitAutosave();
		InitTimelineTools();
		WireViewport();
		WireTimeline();
		WireKeys();
		Closing += Window_Closing;
		Closed += (_, _) => _sound?.Dispose();

		State.Undo.Changed += () =>
		{
			_session.KeepToWhatExists();   // an undo can take the open object away
			_tool.BonesEnabled = _settings.ShowBones && _session.Active?.Kind != ObjectKind.Drawn;   // (given bones, or not any more)
			RefreshRigList();
			RefillProperties();   // (a rename, a new parent, an undo)
			RefreshAnimationsBar();
			RefreshSkins();
			RefreshPoses();
			UpdateTitle();
		};

		State.Playing = false;
		Opened += (_, _) => ResetView();
		RefreshRigList();
		RefreshAnimationsBar();
		RefreshSkins();
		RefreshPoses();
		UpdateTitle();
		_frames = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => OnFrame());
		_frames.Start();
	}

	// A character to build on: a rig with one bone, pointing up, and an empty animation
	private static (Skeleton, Animation) NewCharacter()
	{
		var skeleton = new Skeleton();
		skeleton.Roots.Add(new Bone { Name = "root", Length = 60, SetupRotation = MathF.PI / 2 });
		return (skeleton, new Animation { Name = "animation", Duration = 2f, Loop = true });
	}

	private Scene CharacterScene(Skeleton skeleton, Animation animation)
	{
		var scene = new Scene { Kind = ProjectKind.Character };
		scene.Objects.Add(new SceneObject("Character", ObjectKind.Rigged, skeleton) { Animations = { animation } });
		return scene;
	}

	// The editor settings the tools go by (mouse buttons, snapping)
	private void ApplySettings()
	{
		var s = _settings;
		if (s.MouseButtonsValid)
		{
			_tool.SelectButton = s.SelectButton;
			_tool.TransformButton = s.TransformButton;
			_tool.PanButton = s.PanButton;
		}
		_tool.KeepPartsInPlace = s.KeepPartsInPlace;
		State.KeepAnimations = s.KeepAnimations;
		_snap.ToGrid = s.SnapToGrid;
		_snap.ToGuides = s.SnapToGuides && s.ShowGuides;
		_guides.ShowRulers = s.ShowRulers;
		_guides.ShowGuides = s.ShowGuides;
		_guides.LockGuides = s.LockGuides;
		_painter.ShowGrid = s.ShowGrid;
		_painter.ShowGuides = s.ShowGuides;
		_painter.ShowRulers = s.ShowRulers;
		_painter.LockGuides = s.LockGuides;
		_painter.ShowBones = s.ShowBones;
		_tool.BonesEnabled = s.ShowBones && _session.Active?.Kind != ObjectKind.Drawn;
	}

	// ======================= the layout =======================

	private Control BuildLayout()
	{
		var root = new DockPanel();

		var top = new StackPanel();
		top.Children.Add(BuildMenu());
		top.Children.Add(BuildToolBar());
		var scaledTop = UiSize.Scaled(top);
		DockPanel.SetDock(scaledTop, Dock.Top);
		root.Children.Add(scaledTop);

		var statusBar = new DockPanel { Margin = new Thickness(8, 2), Height = 24 };
		DockPanel.SetDock(_hint, Dock.Right);
		statusBar.Children.Add(_hint);
		statusBar.Children.Add(_status);
		var scaledStatus = UiSize.Scaled(statusBar);
		DockPanel.SetDock(scaledStatus, Dock.Bottom);
		root.Children.Add(scaledStatus);

		// Left: the rig (or the scene's objects); right of it, the viewport over the timeline
		var main = new Grid { ColumnDefinitions = new ColumnDefinitions("280,4,*,4,300") };
		// The rig's bones and parts, its skins, its poses
		LeftTabs = new TabControl
		{
			Items =
			{
				new TabItem { Header = "Rig", FontSize = 15, MinHeight = 32, Padding = new Thickness(10, 4), Content = BuildRigList() },
				new TabItem { Header = "Skins", FontSize = 15, MinHeight = 32, Padding = new Thickness(10, 4), Content = BuildSkinsTab() },
				new TabItem { Header = "Poses", FontSize = 15, MinHeight = 32, Padding = new Thickness(10, 4), Content = BuildPosesTab() }
			}
		};
		var rig = UiSize.Scaled(LeftTabs);
		Grid.SetColumn(rig, 0);
		main.Children.Add(rig);
		var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
		Grid.SetColumn(splitter, 1);
		main.Children.Add(splitter);

		var work = new Grid { RowDefinitions = new RowDefinitions("*,4,Auto,190") };
		// The viewport, and the paint editor beside it while it's open
		var paintColumn = new ColumnDefinition(0, GridUnitType.Pixel);
		var views = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(4, GridUnitType.Pixel), paintColumn } };
		views.Children.Add(Viewport);
		views.Children.Add(_eventFlash);   // (over it: the events passed while playing)
		_paintSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, IsVisible = false };
		Grid.SetColumn(_paintSplitter, 1);
		views.Children.Add(_paintSplitter);
		var paint = BuildPaintPanel(paintColumn);
		Grid.SetColumn(paint, 2);
		views.Children.Add(paint);
		Grid.SetRow(views, 0);
		work.Children.Add(views);
		var timelineSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
		Grid.SetRow(timelineSplitter, 1);
		work.Children.Add(timelineSplitter);
		var animations = UiSize.Scaled(new Panel { Children = { BuildAnimationsBar(), BuildSceneBar() } });   // (inside an object, or at scene level)
		Grid.SetRow(animations, 2);
		work.Children.Add(animations);
		Grid.SetRow(TimelineView, 3);
		work.Children.Add(TimelineView);
		Grid.SetColumn(work, 2);
		main.Children.Add(work);
		var propsSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
		Grid.SetColumn(propsSplitter, 3);
		main.Children.Add(propsSplitter);
		var props = UiSize.Scaled(BuildProperties());
		Grid.SetColumn(props, 4);
		main.Children.Add(props);

		root.Children.Add(main);
		return root;
	}

	// ======================= the frame loop =======================

	private void OnFrame()
	{
		double now = _clock.Elapsed.TotalSeconds;
		float dt = (float)Math.Min(now - _last, 0.1);
		_last = now;
		Frame(dt);
	}

	// One frame: the time goes on, everything drawn again, the panels following (the tests step
	// the window frame by frame this way)
	internal void Frame(float dt)
	{
		if (_exporting) return;   // (the export poses the scene meanwhile)
		Tick(dt);
		UpdateEventFlash();
		SyncColourPanels();
		SyncBoxesPanel();
		SyncScene();
		SyncOnionUi();
		Viewport.InvalidateVisual();
		TimelineView.InvalidateVisual();
		SyncToolBar();
		SyncRigSelection();
		SyncProperties();
		FollowPaint();
		UpdateStatus();
	}

	// The time goes on (when playing), and the scene is posed for it
	internal void Tick(float dt)
	{
		State.PlayRange = _settings.LoopPickedFrames ? _timeline.Range : null;   // (just the picked frames, over and over)
		if (State.Playing && SceneLevel) _session.AdvanceScene(dt * State.Speed);
		else if (State.Playing) FireEvents(State.Advance(dt));
		// Unkeyed changes (auto key off) last as long as the playhead stays on their frame
		State.Hold.KeepOnlyAt(State.Skeleton, State.Animation, State.Frame, Editing && State.AnimateMode && !State.Playing);
		_session.PoseScene(_session.ClockTime);
		UpdateCameraSpace();
		SyncSound();
	}

	// Tools work in the open object's own space; at scene level, in the scene's
	private void UpdateCameraSpace() =>
		_camera.Space = Editing && _session.Active is { } open ? Scene.WorldTransform(open) : Matrix3x2.Identity;

	// F: zoom 1, centred on the open object's origin. At scene level: the whole stage in view
	// (or zoom 1 on the scene's origin, without a stage).
	internal void ResetView()
	{
		UpdateCameraSpace();
		_camera.ViewSize = Viewport.SizeInUnits;
		_camera.Reset();
		if (Editing)
			_camera.Pan = _camera.Space.Translation;
		else if (Scene.HasStage && _camera.ViewSize.X > 0 && _camera.ViewSize.Y > 0)
		{
			float fit = MathF.Min(_camera.ViewSize.X / Scene.StageWidth, _camera.ViewSize.Y / Scene.StageHeight);
			_camera.Zoom = Math.Clamp(fit * 0.9f, 0.1f, 20f);
		}
	}

	// ======================= opening objects =======================

	// Opens an object in place (at the clip of it playing now, or the one double-clicked)
	internal void EnterObject(SceneObject obj, SceneClip? via = null)
	{
		_session.Enter(obj, via);
		_tool.BonesEnabled = _settings.ShowBones && obj.Kind != ObjectKind.Drawn;
		_session.PoseScene(_session.ClockTime);
		UpdateCameraSpace();
		RefreshRigList();
		RefreshAnimationsBar();
		RefreshSkins();
		RefreshPoses();
	}

	// Back to the scene
	internal void LeaveObject()
	{
		if (SceneLevel) return;
		ClosePaintEditor();   // (its drawing belongs to the object)
		_session.Leave();
		UpdateCameraSpace();
		RefreshRigList();
		RefreshAnimationsBar();
		RefreshSkins();
		RefreshPoses();
	}

	// ======================= the status line =======================

	private string _statusText = "";

	private void UpdateStatus()
	{
		string where = SceneLevel ? "Scene" : $"{_session.Active?.Name}: {State.Animation.Name}";
		string mode = SceneLevel ? "" : State.AnimateMode ? "  ·  Animate" : "  ·  Build";
		string frame = SceneLevel ? $"frame {_sceneTimeline.Frame}" : $"frame {State.Frame} / {State.FrameCount}";
		string picked = SceneLevel
			? _session.SceneState.Selected is { } o ? $"  ·  {o.Name}" : ""
			: State.SelectedSlot is { } slot ? $"  ·  {slot.Name}" : State.Selected is { } bone ? $"  ·  {bone.Name}" : "";
		string text = $"{where}{mode}  ·  {frame}{picked}";
		if (text == _statusText) return;
		_statusText = text;
		_status.Text = text;
	}

	internal void Hint(string text) => _hint.Text = text;

	private void UpdateTitle() => Title = $"{_session.DocumentName}{(Unsaved ? " *" : "")} — Harlequin";
}
