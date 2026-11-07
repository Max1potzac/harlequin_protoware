namespace Animator.Viewer.Tools;

// What the mesh tool does in the viewport (the part panel's Mesh box)
public enum MeshMode { None, Points, Weights }

// A mesh edited in the viewport: its points dragged (Build: where they sit on the picture;
// Animate: a deform key at the playhead, the point pushed by how far the mouse went), or its
// weights painted for a bone (Build only: the points under the brush given more of it, or less).
// Editing a skin: the skin's own copy of the drawing changes. Each drag is one undo step.
public sealed class MeshEditTool
{
	private readonly EditorSession _session;
	private readonly Camera _camera;

	private sealed class Drag
	{
		public required Slot Slot;
		public required Attachment Drawing;
		public required int Point;               // -1: painting weights
		public StructureSnapshot? Structure;     // Build
		public AnimationSnapshot? Keys;          // Animate
		public Vector2[]? StartOffsets;
		public Vector2 Start;
		public bool Subtract;
		public bool Changed;
	}
	private Drag? _drag;

	public MeshMode Mode { get; set; }
	public int Hover { get; private set; } = -1;          // the point under the mouse (Points), or -1
	public bool Dragging => _drag != null;

	public Func<Bone?> WeightBone { get; set; } = () => null;   // whose weights are painted
	public float BrushPx { get; set; } = 30f;                   // the weight brush's reach, on screen
	public float Strength { get; set; } = 0.5f;                 // how much a stroke gives (0..1)
	public float PointReachPx { get; set; } = 9f;

	public MeshEditTool(EditorSession session, Camera camera)
	{
		_session = session;
		_camera = camera;
	}

	private EditorState State => _session.State;

	// The point of the mesh at this screen point (-1: none near)
	public int PointAt(Vector2 screen, Slot slot, Attachment drawing, Mesh mesh)
	{
		var posed = MeshOps.Posed(slot, drawing, mesh);
		int best = -1;
		float bestDistance = PointReachPx;
		for (int i = 0; i < posed.Length; i++)
		{
			float d = Vector2.Distance(_camera.WorldToScreen(posed[i]), screen);
			if (d < bestDistance) (best, bestDistance) = (i, d);
		}
		return best;
	}

	// True: the press is the mesh tool's (a point taken hold of, or weights painted). subtract:
	// the weight brush takes away.
	public bool MouseDown(Vector2 screen, PointerButton button, bool subtract)
	{
		if (Mode == MeshMode.None || button != PointerButton.Left || _session.MeshOf(State.SelectedSlot) is not { Mesh: { } mesh } part) return false;
		if (Mode == MeshMode.Weights)
		{
			if (State.AnimateMode || WeightBone() is null) return false;
			_drag = new Drag { Slot = part.Slot, Drawing = part.Drawing, Point = -1, Structure = State.CaptureStructure(), Subtract = subtract };
			if (State.EditSkin != null && State.EditableDrawing(part.Slot) is { } own) _drag.Drawing = own;
			PaintWeightsAt(screen);
			return true;
		}

		int point = PointAt(screen, part.Slot, part.Drawing, mesh);
		if (point < 0) return false;
		var drag = new Drag { Slot = part.Slot, Drawing = part.Drawing, Point = point, Start = _camera.ScreenToWorld(screen) };
		if (State.AnimateMode)
		{
			State.Playing = false;
			State.SetFrame(State.Frame);
			drag.Keys = AnimationSnapshot.Capture(State.Animation);
			drag.StartOffsets = part.Slot.DeformOf(part.Drawing)?.ToArray() ?? new Vector2[mesh.Points.Length];
		}
		else
		{
			drag.Structure = State.CaptureStructure();
			if (State.EditSkin != null && State.EditableDrawing(part.Slot) is { } own) drag.Drawing = own;
		}
		_drag = drag;
		return true;
	}

	// Dragging: the point moves (or weights are painted). Not: which point the mouse is over.
	public void MouseMove(Vector2 screen)
	{
		if (_drag is not { } drag)
		{
			Hover = Mode == MeshMode.Points && _session.MeshOf(State.SelectedSlot) is { Mesh: { } m } p ? PointAt(screen, p.Slot, p.Drawing, m) : -1;
			return;
		}
		if (drag.Point < 0)
		{
			PaintWeightsAt(screen);
			return;
		}
		if (drag.Drawing.Mesh is not { } mesh || drag.Point >= mesh.Points.Length) return;
		var world = _camera.ScreenToWorld(screen);
		if (!Matrix3x2.Invert(MeshOps.PointMatrix(drag.Slot, drag.Drawing, mesh, drag.Point), out var toPixels)) return;
		if (drag.Keys != null && drag.StartOffsets != null)
		{
			// Animate: a deform key here, the point pushed by how far the mouse went (in its pixels)
			var offsets = (Vector2[])drag.StartOffsets.Clone();
			offsets[drag.Point] += Vector2.TransformNormal(world - drag.Start, toPixels);
			MeshOps.KeyDeform(State.Animation, drag.Slot, drag.Drawing, offsets, State.Frame, State.Fps);
		}
		else
			drag.Drawing.Mesh = MeshOps.MovePoint(mesh, drag.Point, Vector2.Transform(world, toPixels));
		drag.Changed = true;
	}

	private void PaintWeightsAt(Vector2 screen)
	{
		if (_drag is not { } drag || drag.Drawing.Mesh is not { Weighted: true } mesh || WeightBone() is not { } bone) return;
		var posed = MeshOps.Posed(drag.Slot, drag.Drawing, mesh);
		float reach = MathF.Max(BrushPx, 1f);
		var touched = new Dictionary<int, float>();
		for (int i = 0; i < posed.Length; i++)
		{
			float d = Vector2.Distance(_camera.WorldToScreen(posed[i]), screen);
			if (d <= reach) touched[i] = 1f - d / reach * 0.7f;   // the middle of the brush, most
		}
		if (touched.Count == 0) return;
		float amount = Strength * 0.35f * (drag.Subtract ? -1f : 1f);
		drag.Drawing.Mesh = MeshOps.Paint(mesh, bone, touched, amount);
		drag.Changed = true;
	}

	// Let go: one undo step, if anything changed (true)
	public bool MouseUp()
	{
		if (_drag is not { } drag) return false;
		_drag = null;
		if (!drag.Changed) return false;
		string name = drag.Point < 0 ? $"Paint weights on {drag.Slot.Name}"
			: drag.Keys != null ? $"Deform {drag.Slot.Name} @ frame {State.Frame}" : $"Move a mesh point of {drag.Slot.Name}";
		if (drag.Keys != null)
			State.Undo.Push(new SnapshotCommand<AnimationSnapshot>(name, drag.Keys, AnimationSnapshot.Capture(State.Animation), s => s.Restore()));
		else if (drag.Structure != null)
			State.PushStructure(drag.Structure, name);
		return true;
	}
}
