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
	private Guide? _guideDrag;                      // the guide being dragged
	private List<(Guide Guide, float At)>? _guidesBefore;   // ...and what they all were before, for undo
	private Vector2? _mouseOnView;                  // for the rulers' red marks

	private const float GuideReachPx = 4f;
	private const float MarkReachPx = 5f;

	private void InitViewAids()
	{
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

	// The status line about them
	private string ViewAidsHelp()
	{
		string K(string id) => KeyOf(id);
		string OnOff(bool on) => on ? "on" : "off";
		var s = _appSettings;
		return $"View: {K("view.rulers")} rulers, {K("view.grid")} grid, {K("view.guides")} guides (drag one out of a ruler, back onto it to take it away; {K("view.lockGuides")}: lock them).   " +
			   $"Snap: {K("view.snapGrid")} to the grid ({OnOff(s.SnapToGrid)}), {K("view.snapGuides")} to guides ({OnOff(s.SnapToGuides)}). Grid size: View menu";
	}

	// ======================= drawing =======================

	// The space the grid and guides are in: the open object's, or the scene's
	private Matrix3x2 AidSpace => Editing ? _camera.Space : Matrix3x2.Identity;

	// The part of that space on screen
	private SKRect VisibleArea()
	{
		var view = _camera.ViewSize;
		var corners = new[] { Vector2.Zero, new Vector2(view.X, 0), view, new Vector2(0, view.Y) }
			.Select(_camera.ScreenToWorld).ToArray();
		var min = corners.Aggregate(Vector2.Min);
		var max = corners.Aggregate(Vector2.Max);
		return new SKRect(min.X, min.Y, max.X, max.Y);
	}

	// Under the scene: the axes' companions
	private void DrawGrid(SKCanvas canvas)
	{
		if (!_appSettings.ShowGrid) return;
		DrawInSpace(canvas, AidSpace, () => ViewAids.DrawGrid(canvas, VisibleArea(), _scene.GridSize, _camera.EditZoom));   // (pixels per unit of that space)
	}

	// Over everything
	private void DrawGuides(SKCanvas canvas)
	{
		if (!_appSettings.ShowGuides || _scene.Guides.Count == 0) return;
		DrawInSpace(canvas, AidSpace, () =>
			ViewAids.DrawGuides(canvas, _scene.Guides, VisibleArea(), _camera.EditZoom, _guideDrag, _appSettings.LockGuides));
	}

	// The canvas in screen units
	private void DrawRulers(SKCanvas canvas)
	{
		if (!_appSettings.ShowRulers) return;
		var shown = _appSettings.ShowGuides ? _scene.Guides : new List<Guide>();
		var top = shown.Where(g => g.Vertical).Select(g => MarkOn(g, top: true)).OfType<float>();
		var left = shown.Where(g => !g.Vertical).Select(g => MarkOn(g, top: false)).OfType<float>();
		ViewAids.DrawRulers(canvas, _camera.ViewSize, _camera.ScreenToWorld, top.ToList(), left.ToList(), _mouseOnView);
	}

	// ======================= where guides are on screen =======================

	// Two screen points on a guide's line
	private (Vector2 A, Vector2 B) OnScreen(Guide guide) => guide.Vertical
		? (_camera.WorldToScreen(new Vector2(guide.At, 0)), _camera.WorldToScreen(new Vector2(guide.At, 100)))
		: (_camera.WorldToScreen(new Vector2(0, guide.At)), _camera.WorldToScreen(new Vector2(100, guide.At)));

	// Where a guide crosses the middle of a ruler: screen x on the top one, y on the left one
	private float? MarkOn(Guide guide, bool top)
	{
		var (a, b) = OnScreen(guide);
		float middle = ViewAids.RulerSize / 2;
		if (top)
		{
			if (MathF.Abs(b.Y - a.Y) < 1e-4f) return null;   // along the ruler: it never crosses it
			return a.X + (middle - a.Y) * (b.X - a.X) / (b.Y - a.Y);
		}
		if (MathF.Abs(b.X - a.X) < 1e-4f) return null;
		return a.Y + (middle - a.X) * (b.Y - a.Y) / (b.X - a.X);
	}

	private static float DistanceToLine(Vector2 p, Vector2 a, Vector2 b)
	{
		var along = b - a;
		float length = along.Length();
		if (length < 1e-6f) return Vector2.Distance(p, a);
		return MathF.Abs(along.X * (p.Y - a.Y) - along.Y * (p.X - a.X)) / length;
	}

	private bool InTopRuler(Vector2 s) => _appSettings.ShowRulers && s.Y < ViewAids.RulerSize && s.X >= ViewAids.RulerSize;
	private bool InLeftRuler(Vector2 s) => _appSettings.ShowRulers && s.X < ViewAids.RulerSize && s.Y >= ViewAids.RulerSize;
	private bool OnRulers(Vector2 s) => _appSettings.ShowRulers && (s.X < ViewAids.RulerSize || s.Y < ViewAids.RulerSize);

	// The guide whose line passes right by this screen point (none when guides are hidden or locked)
	private Guide? GuideAt(Vector2 screen)
	{
		if (!_appSettings.ShowGuides || _appSettings.LockGuides) return null;
		Guide? best = null;
		float bestDistance = GuideReachPx;
		foreach (var guide in _scene.Guides)
		{
			var (a, b) = OnScreen(guide);
			float d = DistanceToLine(screen, a, b);
			if (d <= bestDistance)
			{
				best = guide;
				bestDistance = d;
			}
		}
		return best;
	}

	// The guide marked on a ruler at this point
	private Guide? MarkAt(Vector2 screen, bool top)
	{
		if (!_appSettings.ShowGuides || _appSettings.LockGuides) return null;
		return _scene.Guides
			.Where(g => g.Vertical == top && MarkOn(g, top) is { } at && MathF.Abs(at - (top ? screen.X : screen.Y)) <= MarkReachPx)
			.FirstOrDefault();
	}

	// A bone or part (or an object, at scene level) there to pick: guides give way to it
	private bool SomethingToPickAt(Vector2 screen) => _sceneLevel
		? _objectTool.Pick(_camera.ScreenToScene(screen)) != null
		: _tool.Hit(_camera.ScreenToWorld(screen)) != null;

	// ======================= dragging guides =======================

	// A press in the viewport: on a ruler (a new guide, or the one marked there) or on a guide.
	// true: it's a guide drag (or the rulers' corner), and the tools don't see it.
	private bool GuideMouseDown(Vector2 screen, MouseButton button)
	{
		if (button == _tool.PanButton) return false;
		Guide? guide = null;
		if (OnRulers(screen))
		{
			bool top = InTopRuler(screen);
			if (!top && !InLeftRuler(screen)) return true;   // the corner where they meet: nothing there
			_guidesBefore = GuidesNow();
			guide = MarkAt(screen, top);
			if (guide == null)
			{
				// Out of the top ruler: a guide across; out of the left one: up and down
				var at = _camera.ScreenToWorld(screen);
				guide = new Guide(vertical: !top, top ? at.Y : at.X);
				_scene.Guides.Add(guide);
				if (!_appSettings.ShowGuides) ToggleViewAid("guides");   // a new guide shows
			}
		}
		else
		{
			guide = GuideAt(screen);
			if (guide == null || SomethingToPickAt(screen)) return false;
			_guidesBefore = GuidesNow();
		}
		_guideDrag = guide;
		ApplyViewAids();
		return true;
	}

	private void GuideMouseMove(Vector2 screen)
	{
		if (_guideDrag is not { } guide) return;
		var at = _camera.ScreenToWorld(screen);
		float value = guide.Vertical ? at.X : at.Y;
		// It snaps to the grid (not to the other guides)
		if (_appSettings.SnapToGrid && _scene.GridSize > 0)
		{
			float onGrid = MathF.Round(value / _scene.GridSize) * _scene.GridSize;
			if (MathF.Abs(onGrid - value) * _camera.EditZoom <= _snap.ReachPx) value = onGrid;
		}
		guide.At = value;
	}

	// Let go: back on a ruler (or out of the view), and it's gone
	private void GuideMouseUp(Vector2 screen)
	{
		if (_guideDrag is not { } guide || _guidesBefore is not { } before) return;
		var view = _camera.ViewSize;
		bool outside = screen.X < 0 || screen.Y < 0 || screen.X > view.X || screen.Y > view.Y;
		if (OnRulers(screen) || outside) _scene.Guides.Remove(guide);
		_guideDrag = null;
		_guidesBefore = null;

		var after = GuidesNow();
		if (after.SequenceEqual(before)) return;
		bool added = after.Count > before.Count, removed = after.Count < before.Count;
		string name = added ? "Add a guide" : removed ? "Take a guide away" : "Move a guide";
		_state.Undo.Push(new SnapshotCommand<List<(Guide Guide, float At)>>(name, before, after, RestoreGuides));
	}

	private List<(Guide Guide, float At)> GuidesNow() => _scene.Guides.Select(g => (g, g.At)).ToList();

	private void RestoreGuides(List<(Guide Guide, float At)> guides)
	{
		_scene.Guides.Clear();
		foreach (var (guide, at) in guides)
		{
			guide.At = at;
			_scene.Guides.Add(guide);
		}
	}

	private void ClearGuides()
	{
		if (_scene.Guides.Count == 0) return;
		var before = GuidesNow();
		_scene.Guides.Clear();
		_state.Undo.Push(new SnapshotCommand<List<(Guide Guide, float At)>>("Clear the guides", before, GuidesNow(), RestoreGuides));
	}

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
		if (!(size > 0) || MathF.Abs(size - _scene.GridSize) < 1e-6f) return;
		float before = _scene.GridSize;
		_scene.GridSize = size;
		_state.Undo.Push(new SnapshotCommand<float>($"Grid size {size.ToString("0.##", CultureInfo.CurrentCulture)}", before, size, v => _scene.GridSize = v));
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
