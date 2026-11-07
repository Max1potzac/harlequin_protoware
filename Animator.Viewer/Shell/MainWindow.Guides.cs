using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkiaSharp;

namespace Animator.Viewer;

// Rulers, the grid and guides (the View menu). Drag out of a ruler to make a guide (from the top
// one: across; from the left one: up and down); drag a guide, or its mark on a ruler, to move it;
// drag it back onto a ruler to take it away. Joints, bone ends, parts and objects dragged near a
// grid line or a guide land on it (snapping). The grid's size and the guides are saved with the
// project; whether they show and snap is remembered for every project.
public partial class MainWindow
{
	private readonly Snapping _snap = new();
	private ViewportPainter _painter = null!;          // what the viewport shows (see ViewportPainter)
	private GuideTool _guides = null!;              // the rulers and guides, dragged (see GuideTool)
	private Vector2? _mouseOnView;                  // for the rulers' red marks
	private Guide? _guideDrag => _guides?.Dragging;

	private void InitViewAids()
	{
		_guides = new GuideTool(_session, _camera) { Snap = _snap, SomethingToPickAt = SomethingToPickAt };
		_painter = new ViewportPainter(_session, _camera, _renderer, _tool, _objectTool, _guides, _mesh)
		{
			Onion = _onion,
			WeightBone = WeightBone,
			PoseScene = PoseScene   // (the mixer playing poses the open object)
		};
		_paintCanvas.LightTable = _painter.LightTable;   // the part's other drawings under the one painted
		_tool.Snap = _snap;
		_objectTool.Snap = _snap;
		ApplyViewAids();
	}

	// What the settings say, everywhere it matters
	private void ApplyViewAids()
	{
		var s = _appSettings;
		_snap.ToGrid = s.SnapToGrid;
		_snap.ToGuides = s.SnapToGuides && s.ShowGuides;
		_snap.GridSize = _scene.GridSize;
		_snap.Guides = _scene.Guides;
		if (_guides != null)
		{
			_guides.ShowRulers = s.ShowRulers;
			_guides.ShowGuides = s.ShowGuides;
			_guides.LockGuides = s.LockGuides;
		}
		// The text over the viewport keeps clear of the rulers
		float edge = s.ShowRulers ? ViewAids.RulerSize + 8 : 10;
		Status.Margin = new Thickness(edge, 10, 10, 10);
		EventFlash.Margin = new Thickness(12, s.ShowRulers ? ViewAids.RulerSize + 6 : 12, 12, 12);
		ViewToolBar.Margin = s.ShowRulers ? new Thickness(ViewAids.RulerSize + 6, ViewAids.RulerSize + 6, 0, 0) : new Thickness(6);
	}

	private void ToggleViewAid(string what)
	{
		var s = _appSettings;
		switch (what)
		{
			case "rulers": s.ShowRulers = !s.ShowRulers; break;
			case "grid": s.ShowGrid = !s.ShowGrid; break;
			case "guides": s.ShowGuides = !s.ShowGuides; break;
			case "snapGrid": s.SnapToGrid = !s.SnapToGrid; break;
			case "snapGuides": s.SnapToGuides = !s.SnapToGuides; break;
			case "lockGuides": s.LockGuides = !s.LockGuides; break;
		}
		s.Save();
		ApplyViewAids();
	}

	// View → Bones: the open object's armature shown, or hidden (and then not picked in the viewport)
	private void ToggleBones()
	{
		_appSettings.ShowBones = !_appSettings.ShowBones;
		_appSettings.Save();
		if (_tool is not null && _active is { } open) _tool.BonesEnabled = BonesUsable(open);
		AnimHint.Text = _appSettings.ShowBones ? "" : $"Bones hidden ({KeyOf("view.bones")} shows them again).";
	}

	// A drawn object's one bone stays hidden; any other's bones show unless View → Bones is off
	private bool BonesUsable(SceneObject obj) => obj.Kind != ObjectKind.Drawn && _appSettings.ShowBones;

	// The status line about them
	private string ViewAidsHelp()
	{
		string K(string id) => KeyOf(id);
		string OnOff(bool on) => on ? "on" : "off";
		var s = _appSettings;
		return $"View: {K("view.rulers")} rulers, {K("view.grid")} grid, {K("view.guides")} guides (drag one out of a ruler, back onto it to take it away; {K("view.lockGuides")}: lock them).   " +
			   $"Snap: {K("view.snapGrid")} to the grid ({OnOff(s.SnapToGrid)}), {K("view.snapGuides")} to guides ({OnOff(s.SnapToGuides)}). Grid size: View menu";
	}

	// ======================= where guides are on screen (see GuideTool) =======================

	private bool InTopRuler(Vector2 s) => _guides.InTopRuler(s);
	private bool InLeftRuler(Vector2 s) => _guides.InLeftRuler(s);
	private Guide? GuideAt(Vector2 screen) => _guides.GuideAt(screen);
	private Guide? MarkAt(Vector2 screen, bool top) => _guides.MarkAt(screen, top);

	// A bone or part (or an object, at scene level) there to pick: guides give way to it
	private bool SomethingToPickAt(Vector2 screen) => _sceneLevel
		? _objectTool.Pick(_camera.ScreenToScene(screen)) != null
		: _tool.Hit(_camera.ScreenToWorld(screen)) != null;

	// ======================= dragging guides =======================

	// A press in the viewport: on a ruler (a new guide, or the one marked there) or on a guide.
	// true: it's a guide drag (or the rulers' corner), and the tools don't see it.
	private bool GuideMouseDown(Vector2 screen, PointerButton button)
	{
		if (button == _tool.PanButton) return false;
		var press = _guides.MouseDown(screen);
		if (press == GuideTool.Press.NotMine) return false;
		if (press == GuideTool.Press.NewGuide && !_appSettings.ShowGuides) ToggleViewAid("guides");   // a new guide shows
		ApplyViewAids();
		return true;
	}

	private void GuideMouseMove(Vector2 screen) => _guides.MouseMove(screen);

	// Let go: back on a ruler (or out of the view), and it's gone
	private void GuideMouseUp(Vector2 screen) => _guides.MouseUp(screen);

	private List<(Guide Guide, float At)> GuidesNow() => _session.GuidesNow();

	private void ClearGuides() => _session.ClearGuides();

	// The mouse over the viewport, not dragging: the pointer says what a press would do
	private void ShowViewPointer(Vector2 screen)
	{
		Cursor? pointer = null;
		if (InTopRuler(screen)) pointer = MarkAt(screen, true) != null ? Cursors.SizeWE : Cursors.SizeNS;
		else if (InLeftRuler(screen)) pointer = MarkAt(screen, false) != null ? Cursors.SizeNS : Cursors.SizeWE;
		else if (GuideAt(screen) is { } guide && !SomethingToPickAt(screen)) pointer = guide.Vertical ? Cursors.SizeWE : Cursors.SizeNS;
		if (Viewport.Cursor != pointer) Viewport.Cursor = pointer;
	}

	// ======================= grid size =======================

	private static readonly float[] GridSizes = { 1, 2, 4, 5, 8, 10, 16, 20, 25, 32, 50, 64, 100 };

	private void SetGridSize(float size)
	{
		if (!_session.SetGridSize(size, $"Grid size {size.ToString("0.##", CultureInfo.CurrentCulture)}")) return;
		if (!_appSettings.ShowGrid) ToggleViewAid("grid");   // so you see what changed
		ApplyViewAids();
	}

	private void AskGridSize()
	{
		var dialog = new InputDialog("Grid size", "How far apart the grid's lines are, in the units the panels measure in (1 = a pixel of art at full size):",
			_scene.GridSize.ToString("0.###", CultureInfo.CurrentCulture)) { Owner = this };
		if (dialog.ShowDialog() != true) return;
		if (float.TryParse(dialog.Answer, NumberStyles.Float, CultureInfo.CurrentCulture, out var size) ||
			float.TryParse(dialog.Answer, NumberStyles.Float, CultureInfo.InvariantCulture, out size))
			SetGridSize(size);
	}

	// ======================= the View menu =======================

	private void ViewMenu_Opened(object sender, RoutedEventArgs e)
	{
		if (!ReferenceEquals(e.OriginalSource, ViewMenu)) return;   // Grid size opening (it bubbles up here): already built
		var s = _appSettings;
		foreach (var item in MenuItems(ViewMenu.Items))
		{
			item.IsChecked = item.Tag switch
			{
				"view.rulers" => s.ShowRulers,
				"view.grid" => s.ShowGrid,
				"view.guides" => s.ShowGuides,
				"view.snapGrid" => s.SnapToGrid,
				"view.snapGuides" => s.SnapToGuides,
				"view.lockGuides" => s.LockGuides,
				"view.images" => _renderer.ShowImages,
				"view.bones" => _appSettings.ShowBones,
				"view.pixels" => _renderer.PixelArt,
				"view.onion" => _onion.Enabled,
				"view.hitboxes" => _showHitboxes,
				_ => false
			};
		}
		ClearGuidesItem.IsEnabled = _scene.Guides.Count > 0;

		GridSizeMenu.Items.Clear();
		foreach (var size in GridSizes)
		{
			var item = new MenuItem
			{
				Header = size == 1 ? "1 (a pixel)" : size.ToString(CultureInfo.CurrentCulture),
				IsChecked = MathF.Abs(size - _scene.GridSize) < 1e-6f
			};
			float chosen = size;
			item.Click += (_, _) => SetGridSize(chosen);
			GridSizeMenu.Items.Add(item);
		}
		GridSizeMenu.Items.Add(new Separator());
		var other = new MenuItem { Header = GridSizes.Any(g => MathF.Abs(g - _scene.GridSize) < 1e-6f) ? "Other…" : $"Other… ({_scene.GridSize.ToString("0.###", CultureInfo.CurrentCulture)})" };
		other.Click += (_, _) => AskGridSize();
		GridSizeMenu.Items.Add(other);
	}

	private void ClearGuides_Click(object sender, RoutedEventArgs e) => ClearGuides();
}
