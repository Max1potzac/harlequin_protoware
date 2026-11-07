using System.Numerics;

namespace Animator.Viewer.Tools;

// The rulers and guides in the viewport (see ViewAids): where a guide is on screen, and dragging
// them. Out of a ruler comes a guide (from the top one: across; from the left one: up and down);
// a guide, or its mark on a ruler, is dragged to move it; dropped back on a ruler (or out of the
// view), it's gone. Each drag is one undo step (EditorSession.GuidesChanged). The window says
// what shows (the settings) and what the press would otherwise pick.
public sealed class GuideTool
{
	private readonly EditorSession _session;
	private readonly Camera _camera;
	private List<(Guide Guide, float At)>? _before;   // every guide before the drag, for undo

	public const float GuideReachPx = 4f;
	public const float MarkReachPx = 5f;

	public bool ShowRulers { get; set; } = true;
	public bool ShowGuides { get; set; } = true;
	public bool LockGuides { get; set; }
	public Snapping? Snap { get; set; }                // a dragged guide snaps to the grid (not to the other guides)
	public Func<Vector2, bool> SomethingToPickAt { get; set; } = _ => false;   // a bone, part or object there: guides give way

	public Guide? Dragging { get; private set; }

	public GuideTool(EditorSession session, Camera camera)
	{
		_session = session;
		_camera = camera;
	}

	private IReadOnlyList<Guide> Guides => _session.Scene.Guides;

	// ======================= where things are =======================

	public bool InTopRuler(Vector2 s) => ShowRulers && s.Y < ViewAids.RulerSize && s.X >= ViewAids.RulerSize;
	public bool InLeftRuler(Vector2 s) => ShowRulers && s.X < ViewAids.RulerSize && s.Y >= ViewAids.RulerSize;
	public bool OnRulers(Vector2 s) => ShowRulers && (s.X < ViewAids.RulerSize || s.Y < ViewAids.RulerSize);

	// Two screen points on a guide's line
	public (Vector2 A, Vector2 B) OnScreen(Guide guide) => guide.Vertical
		? (_camera.WorldToScreen(new Vector2(guide.At, 0)), _camera.WorldToScreen(new Vector2(guide.At, 100)))
		: (_camera.WorldToScreen(new Vector2(0, guide.At)), _camera.WorldToScreen(new Vector2(100, guide.At)));

	// Where a guide crosses the middle of a ruler: screen x on the top one, y on the left one
	public float? MarkOn(Guide guide, bool top)
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

	// The guide whose line passes right by this screen point (none when guides are hidden or locked)
	public Guide? GuideAt(Vector2 screen)
	{
		if (!ShowGuides || LockGuides) return null;
		Guide? best = null;
		float bestDistance = GuideReachPx;
		foreach (var guide in Guides)
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
	public Guide? MarkAt(Vector2 screen, bool top)
	{
		if (!ShowGuides || LockGuides) return null;
		return Guides.FirstOrDefault(g => g.Vertical == top && MarkOn(g, top) is { } at && MathF.Abs(at - (top ? screen.X : screen.Y)) <= MarkReachPx);
	}

	// The marks on the rulers of the guides that show: screen x along the top one, y down the left one
	public (List<float> Top, List<float> Left) Marks()
	{
		var shown = ShowGuides ? Guides : Array.Empty<Guide>();
		return (shown.Where(g => g.Vertical).Select(g => MarkOn(g, top: true)).OfType<float>().ToList(),
				shown.Where(g => !g.Vertical).Select(g => MarkOn(g, top: false)).OfType<float>().ToList());
	}

	// ======================= dragging =======================

	public enum Press { NotMine, Taken, NewGuide }

	// A press in the viewport: on a ruler (a new guide, or the one marked there) or on a guide.
	// NotMine: the tools have it. Taken: a guide drag (or the rulers' corner, where nothing is).
	// NewGuide: a guide made out of a ruler, and dragged (the window shows guides, if hidden).
	public Press MouseDown(Vector2 screen)
	{
		if (Dragging != null) return Press.Taken;
		Guide? guide;
		bool made = false;
		if (OnRulers(screen))
		{
			bool top = InTopRuler(screen);
			if (!top && !InLeftRuler(screen)) return Press.Taken;   // the corner where they meet: nothing there
			_before = _session.GuidesNow();
			guide = MarkAt(screen, top);
			if (guide == null)
			{
				// Out of the top ruler: a guide across; out of the left one: up and down
				var at = _camera.ScreenToWorld(screen);
				guide = new Guide(vertical: !top, top ? at.Y : at.X);
				_session.Scene.Guides.Add(guide);
				made = true;
			}
		}
		else
		{
			guide = GuideAt(screen);
			if (guide == null || SomethingToPickAt(screen)) return Press.NotMine;
			_before = _session.GuidesNow();
		}
		Dragging = guide;
		return made ? Press.NewGuide : Press.Taken;
	}

	public void MouseMove(Vector2 screen)
	{
		if (Dragging is not { } guide) return;
		var at = _camera.ScreenToWorld(screen);
		float value = guide.Vertical ? at.X : at.Y;
		float grid = _session.Scene.GridSize;
		if (Snap is { ToGrid: true } snap && grid > 0)
		{
			float onGrid = MathF.Round(value / grid) * grid;
			if (MathF.Abs(onGrid - value) * _camera.EditZoom <= snap.ReachPx) value = onGrid;
		}
		guide.At = value;
	}

	// Let go: back on a ruler (or out of the view), and it's gone. True: the guides changed.
	public bool MouseUp(Vector2 screen)
	{
		if (Dragging is not { } guide || _before is not { } before) return false;
		var view = _camera.ViewSize;
		bool outside = screen.X < 0 || screen.Y < 0 || screen.X > view.X || screen.Y > view.Y;
		if (OnRulers(screen) || outside) _session.Scene.Guides.Remove(guide);
		Dragging = null;
		_before = null;
		return _session.GuidesChanged(before);
	}
}
