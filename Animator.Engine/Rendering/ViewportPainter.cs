using System.Numerics;
using Animator.Viewer.Painting;
using Animator.Viewer.Tools;
using SkiaSharp;

namespace Animator.Viewer.Rendering;

// What the viewport shows, drawn: the axes and the grid, the stage, the onion ghosts, every
// object (the others faded while one is open), the open object's bones and handles, its hitboxes
// and mesh, or at scene level the objects' origins and the selected one's gizmo; then the guides,
// and the rulers in screen units. The editor sets what shows (the View menu's settings) and calls
// Draw for each frame of its viewport. Also the paint editor's light table (LightTable).
public sealed class ViewportPainter
{
	private readonly EditorSession _session;
	private readonly Camera _camera;
	private readonly SkeletonRenderer _renderer;
	private readonly BoneTool _tool;
	private readonly ObjectTool _objectTool;
	private readonly GuideTool _guides;
	private readonly MeshEditTool _mesh;

	public const float FadedOpacity = 0.35f;   // the other objects, while one is open

	public OnionSkin Onion { get; set; } = new();

	// What shows (the View menu)
	public bool ShowGrid { get; set; }
	public bool ShowGuides { get; set; } = true;
	public bool ShowRulers { get; set; } = true;
	public bool LockGuides { get; set; }
	public bool ShowBones { get; set; } = true;
	public bool ShowHitboxes { get; set; }          // (the Boxes view shows them too)
	public bool BoxesView { get; set; }             // the Boxes view open: the picked and hovered box stand out
	public object? BoxPicked { get; set; }
	public object? BoxHover { get; set; }
	public Vector2? MouseOnView { get; set; }       // for the rulers' marks
	public Func<Bone?> WeightBone { get; set; } = () => null;   // the mesh's weights shown for it
	public Action<float> PoseScene { get; set; }    // the scene posed at a time (the ghosts pose it, then put it back)

	public ViewportPainter(EditorSession session, Camera camera, SkeletonRenderer renderer, BoneTool tool, ObjectTool objectTool,
		GuideTool guides, MeshEditTool mesh)
	{
		_session = session;
		_camera = camera;
		_renderer = renderer;
		_tool = tool;
		_objectTool = objectTool;
		_guides = guides;
		_mesh = mesh;
		PoseScene = time => session.PoseScene(time);
	}

	private Scene Scene => _session.Scene;
	private EditorState State => _session.State;
	private Skeleton Rig => State.Skeleton;
	private bool Editing => _session.Editing;
	private bool SceneLevel => _session.SceneLevel;
	private bool IsDrawn => _session.Active?.Kind == ObjectKind.Drawn;

	private static readonly HashSet<Bone> NoBones = new();
	private static readonly HashSet<Slot> NoSlots = new();

	// The whole viewport: the canvas in device pixels, `dpi` of them to a screen unit, `view` screen
	// units across. (The background is the caller's.)
	public void Draw(SKCanvas canvas, Vector2 view, float dpi)
	{
		canvas.Scale(dpi);
		_camera.ViewSize = view;
		_camera.Apply(canvas);   // scene space from here on

		_renderer.Zoom = _camera.Zoom;
		_renderer.Selected = State.Selected;
		_renderer.SelectedSlot = State.SelectedSlot;
		_renderer.HideHidden = !SceneLevel;   // bones and parts hidden with their eyes: while editing
		_renderer.SkipLocked = !SceneLevel;   // ...and locked ones can't be picked
		_renderer.PickedBones = SceneLevel ? NoBones : State.PickedBones.ToHashSet();
		_renderer.PickedSlots = SceneLevel ? NoSlots : State.PickedSlots.ToHashSet();
		_renderer.SelectionBox = SceneLevel ? null : _tool.SelectionBox;
		_renderer.ShowPivotHandle = !State.AnimateMode && State.Picked.Count <= 1;   // in Animate mode, part drags set placement keys
		_renderer.ShowBones = !IsDrawn && ShowBones;   // a drawn object's one bone stays hidden
		_renderer.PartsOf = !SceneLevel && !IsDrawn && State.SelectedSlot == null ? State.Selected : null;   // what's on the selected bone
		_renderer.GhostBone = _tool.CreatePreview;
		_renderer.TipHandleOf = !SceneLevel && !IsDrawn ? _tool.TipHandle : null;   // Setup mode: where the selected bone ends
		DrawScene(canvas);
		DrawGuides(canvas);

		canvas.ResetMatrix();
		canvas.Scale(dpi);   // screen units from here on
		DrawRulers(canvas);
	}

	// Draws in an object's space. The renderer keeps lines and handles the same size on screen.
	public void DrawInSpace(SKCanvas canvas, Matrix3x2 space, Action draw)
	{
		var m = SkeletonRenderer.ToSk(space);
		canvas.Save();
		canvas.Concat(in m);
		_renderer.Zoom = _camera.Zoom * ScaleOf(space);
		draw();
		canvas.Restore();
		_renderer.Zoom = _camera.Zoom;
	}

	private static float ScaleOf(Matrix3x2 m)
	{
		float det = MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21);
		return det > 1e-12f ? MathF.Sqrt(det) : 1f;
	}

	// ======================= the scene =======================

	// The canvas is in scene space here. Objects are drawn in list order (first = furthest back);
	// while you edit one, the others are faded and its bones and handles go on top of everything.
	private void DrawScene(SKCanvas canvas)
	{
		// The axes the panel's numbers are measured from: the open object's origin, or the scene's
		DrawInSpace(canvas, Editing ? _camera.Space : Matrix3x2.Identity, () => _renderer.DrawAxes(canvas));
		DrawGrid(canvas);   // in the same units, behind everything
		if (Editing) DrawStage(canvas);   // inside an object: just the frame's outline, behind
		DrawOnionGhosts(canvas);

		foreach (var obj in Scene.Objects)
		{
			bool open = Editing && obj == _session.Active;
			if (!obj.Visible && !open) continue;   // a hidden object still shows while you work on it
			DrawInSpace(canvas, Scene.WorldTransform(obj), () =>
			{
				if (Editing && !open) _renderer.DrawFaded(canvas, obj.Skeleton, FadedOpacity);
				else _renderer.DrawParts(canvas, obj.Skeleton);
				if (!Editing && obj.Kind == ObjectKind.Rigged && _renderer.ImageBounds(obj.Skeleton) == null)
					_renderer.DrawBoneHints(canvas, obj.Skeleton);   // a rig with no pictures yet
			});
		}

		if (Editing)
		{
			DrawInSpace(canvas, _camera.Space, () => _renderer.DrawOverlays(canvas, Rig));
			DrawHitboxes(canvas);
			DrawMeshOverlay(canvas);
			return;
		}

		DrawStage(canvas);   // at scene level, what's outside the frame is dimmed

		// Scene level: every object's origin as a faint cross (so even an empty one can be found)
		foreach (var obj in Scene.Objects.Where(o => o.Visible))
			ObjectGizmo.DrawOrigin(canvas, Vector2.Transform(Vector2.Zero, Scene.WorldTransform(obj)), _camera.Zoom);

		if (_objectTool.Selected is { Visible: true } selected && Scene.Objects.Contains(selected))
		{
			ObjectGizmo.Draw(canvas, ObjectGizmo.For(Scene, selected, _renderer, _camera.Zoom), _camera.Zoom, handles: !selected.Locked);
			// Carried by a bone: a dashed line from that bone's joint to the object's origin
			if (Scene.AttachmentBone(selected) is { } bone && Scene.Find(selected.AttachObject!) is { } holder)
				ObjectGizmo.DrawAttachLine(canvas,
					Vector2.Transform(bone.World.Translation, Scene.WorldTransform(holder)),
					Vector2.Transform(Vector2.Zero, Scene.WorldTransform(selected)), _camera.Zoom);
		}
	}

	// The stage: the frame the scene is made for (an animation project's video size), centred on
	// the scene's origin. At scene level everything outside it is dimmed.
	private void DrawStage(SKCanvas canvas)
	{
		if (!Scene.HasStage) return;
		float w = Scene.StageWidth / 2f, h = Scene.StageHeight / 2f, px = 1f / _camera.Zoom, far = 1e6f;
		if (!Editing)
		{
			using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 100) };
			canvas.DrawRect(new SKRect(-far, h, far, far), dim);     // above (y points up)
			canvas.DrawRect(new SKRect(-far, -far, far, -h), dim);   // below
			canvas.DrawRect(new SKRect(-far, -h, -w, h), dim);       // left
			canvas.DrawRect(new SKRect(w, -h, far, h), dim);         // right
		}
		using var edge = new SKPaint
		{
			Color = new SKColor(255, 255, 255, Editing ? (byte)45 : (byte)120),
			Style = SKPaintStyle.Stroke,
			StrokeWidth = 1.5f * px,
			IsAntialias = true
		};
		canvas.DrawRect(new SKRect(-w, -h, w, h), edge);
	}

	// ======================= onion skin =======================

	// The ghosts behind everything, farthest first. Inside an object only that object gets them; at
	// scene level, every visible object with "Onion skin" ticked. Each ghost poses the scene at its
	// own frame; afterwards everything goes back to the frame you're on. Not while playing.
	private void DrawOnionGhosts(SKCanvas canvas)
	{
		if (!Onion.Enabled || State.Playing || !_renderer.ShowImages) return;
		var ghosted = Scene.Objects.Where(o => Editing ? o == _session.Active : o.Visible && o.Onion).ToList();
		if (ghosted.Count == 0) return;

		float clock = _session.ClockTime;
		int now = (int)MathF.Round(clock * State.Fps);
		bool drew = false;
		for (int k = Onion.Before; k >= 1; k--)
			drew |= DrawGhostsAt(canvas, ghosted, now - k * Onion.Step, now, before: true, k, Onion.Before);
		for (int k = Onion.After; k >= 1; k--)
			drew |= DrawGhostsAt(canvas, ghosted, now + k * Onion.Step, now, before: false, k, Onion.After);

		if (drew) PoseScene(clock);
	}

	private bool DrawGhostsAt(SKCanvas canvas, List<SceneObject> ghosted, int frame, int now, bool before, int distance, int count)
	{
		// Frames of the animation being edited, or of the scene timeline: a loop wraps around
		int frames = Editing ? State.FrameCount : (int)MathF.Round(Scene.Duration * State.Fps);
		bool loops = Editing ? State.Animation.Loop : Scene.Loop;
		if (frames <= 0) return false;
		if (loops) frame = (frame % frames + frames) % frames;
		else if (frame < 0 || frame > frames) return false;
		if (frame == now) return false;

		PoseScene(frame / (float)State.Fps);   // all of it, so attached objects follow their holders
		float opacity = OnionSkin.Opacity(distance, count);
		foreach (var obj in ghosted)
			DrawInSpace(canvas, Scene.WorldTransform(obj), () => _renderer.DrawGhost(canvas, obj.Skeleton, before, opacity));
		return true;
	}

	// The paint editor's light table: the part's previous and next drawings, placed relative to the
	// drawing being painted (all drawings of a part sit on the same bone, so only their own
	// placements matter)
	public IReadOnlyList<LightTableImage> LightTable()
	{
		var images = new List<LightTableImage>();
		var paint = _session.Paint;
		if (!Onion.Enabled || State.Playing || _session.PaintSlot is not { } slot || paint.Asset is not { } open)
			return images;

		var painted = Rig.AllSkins().SelectMany(s => s.AttachmentsFor(slot.Name)).FirstOrDefault(a => a.AssetId == open.Id);
		if (painted == null || !Matrix3x2.Invert(painted.LocalMatrix(open.Width, open.Height), out var toPainted))
			return images;

		var (earlier, later) = DrawingOps.Neighbors(State.Animation, slot, State.Time, Onion.Before, Onion.After);
		AddGhosts(earlier, before: true, Onion.Before);
		AddGhosts(later, before: false, Onion.After);
		return images;

		void AddGhosts(List<string> names, bool before, int count)
		{
			for (int k = names.Count; k >= 1; k--)   // farthest first, so the nearest ends up on top
			{
				if (DrawingOps.Find(Rig, slot, names[k - 1]) is not { } drawing
					|| drawing.AssetId == open.Id
					|| !Rig.Assets.TryGetValue(drawing.AssetId, out var asset)
					|| _session.Assets.Get(asset) is not { } bitmap)
					continue;

				var placement = drawing.LocalMatrix(asset.Width, asset.Height) * toPainted;
				images.Add(new LightTableImage(bitmap, placement, before, OnionSkin.Opacity(k, count)));
			}
		}
	}

	// ======================= the open object's boxes and mesh =======================

	private bool ShowingHitboxes => !SceneLevel && (ShowHitboxes || BoxesView);

	private void DrawHitboxes(SKCanvas canvas)
	{
		if (!ShowingHitboxes || (Rig.Hitboxes.Count == 0 && Rig.Points.Count == 0)) return;
		DrawInSpace(canvas, _camera.Space, () =>
			HitboxRenderer.Draw(canvas, Rig, _renderer.Zoom, BoxesView ? BoxPicked : null, BoxesView ? BoxHover : null));
	}

	private void DrawMeshOverlay(SKCanvas canvas)
	{
		if (_mesh.Mode == MeshMode.None || _session.MeshOf(State.SelectedSlot) is not { Mesh: { } mesh } part) return;
		DrawInSpace(canvas, _camera.Space, () =>
		{
			float px = 1f / MathF.Max(_renderer.Zoom, 1e-4f);
			var posed = MeshOps.Posed(part.Slot, part.Drawing, mesh);
			using var edge = new SKPaint { Color = new SKColor(255, 255, 255, 110), Style = SKPaintStyle.Stroke, StrokeWidth = 1f * px, IsAntialias = true };
			var t = mesh.Triangles;
			for (int i = 0; i < t.Length; i += 3)
			{
				using var path = ShapePaths.Polyline(new[] { posed[t[i]], posed[t[i + 1]], posed[t[i + 2]] }, closed: true);
				canvas.DrawPath(path, edge);
			}
			var bone = _mesh.Mode == MeshMode.Weights ? WeightBone() : null;
			using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
			using var ring = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1f * px, IsAntialias = true };
			for (int i = 0; i < posed.Length; i++)
			{
				dot.Color = bone != null ? Heat(MeshOps.WeightOf(mesh, i, bone)) : i == _mesh.Hover ? SKColors.Yellow : SKColors.White;
				float r = (bone != null ? 4.5f : 3.5f) * px;
				canvas.DrawCircle(posed[i].X, posed[i].Y, r, dot);
				canvas.DrawCircle(posed[i].X, posed[i].Y, r, ring);
			}
		});
	}

	// Blue (none) → green → red (all of it)
	public static SKColor Heat(float weight)
	{
		weight = Math.Clamp(weight, 0f, 1f);
		return weight < 0.5f
			? new SKColor(0, (byte)(weight * 2 * 255), (byte)((1 - weight * 2) * 255))
			: new SKColor((byte)((weight - 0.5f) * 2 * 255), (byte)((1 - (weight - 0.5f) * 2) * 255), 0);
	}

	// ======================= grid, guides, rulers =======================

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

	private void DrawGrid(SKCanvas canvas)
	{
		if (!ShowGrid) return;
		DrawInSpace(canvas, AidSpace, () => ViewAids.DrawGrid(canvas, VisibleArea(), Scene.GridSize, _camera.EditZoom));   // (pixels per unit of that space)
	}

	private void DrawGuides(SKCanvas canvas)
	{
		if (!ShowGuides || Scene.Guides.Count == 0) return;
		DrawInSpace(canvas, AidSpace, () =>
			ViewAids.DrawGuides(canvas, Scene.Guides, VisibleArea(), _camera.EditZoom, _guides.Dragging, LockGuides));
	}

	// The canvas in screen units
	private void DrawRulers(SKCanvas canvas)
	{
		if (!ShowRulers) return;
		var (top, left) = _guides.Marks();
		ViewAids.DrawRulers(canvas, _camera.ViewSize, _camera.ScreenToWorld, top, left, MouseOnView);
	}
}
