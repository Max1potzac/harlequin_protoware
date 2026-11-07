using Animator.Viewer.Painting;

namespace Animator.Viewer;

// What the paint editor shows, given a fresh paint session per editor: the session's PaintSession
// (the open drawing, its layer, the brush and colours) and its tools; the part whose drawing it
// is, so the editor follows that part's drawings along the timeline; and opening and closing it.
// The window keeps the canvas (zoom, the mouse) and the panel.
public sealed partial class EditorSession
{
	private PaintSession? _paint;

	public PaintToolbox PaintTools { get; } = new();

	// (made the first time it's asked for, on the open object's rig, with the brush in hand)
	public PaintSession Paint => _paint ??= new PaintSession(Assets, State.Undo, State.Skeleton, PaintTools.Brush);

	public Slot? PaintSlot { get; private set; }        // the part whose drawing is open (null: none, or a picture of its own)
	public Asset? PaintFollowed { get; private set; }   // the drawing that part showed when the editor last looked

	public bool PaintOpen => Paint.Asset != null;

	// A half-placed figure (or move) lands, before anything else happens to the drawing
	public void FinishPaintTool() => Paint.Tool.Deactivate(Paint);

	// The paint tool of that name in hand (a half-made figure with the last one lands). False: no such tool.
	public bool SelectPaintTool(string name)
	{
		if (PaintTools.Named(name) is not { } tool) return false;
		if (tool != Paint.Tool) FinishPaintTool();
		Paint.Tool = tool;
		return true;
	}

	// The picture a part shows in the current pose (null: hidden, or it has none)
	public Asset? PictureOf(Slot slot) =>
		Rig.ResolveAttachment(slot) is { } shown && Rig.Assets.TryGetValue(shown.AssetId, out var asset) ? asset : null;

	// A drawing open in the editor. slot: the part it belongs to, so the editor follows that part's
	// drawings (another drawing of the same part stays on the same layer number: lines, colours…).
	public void OpenPaint(Asset asset, Slot? slot)
	{
		FinishPaintTool();
		int layer = slot != null && slot == PaintSlot && Paint.Asset is { } previous && Paint.Layer is { } current
			? previous.Layers.IndexOf(current)
			: -1;
		Paint.Open(asset, layer);
		PaintSlot = slot;
		PaintFollowed = slot != null ? PictureOf(slot) : null;
	}

	// The part's drawing open in the editor (editing a skin: the skin's own pixels, see
	// PixelsToPaint). False: the part shows no picture.
	public bool OpenPaintOn(Slot slot)
	{
		if (PictureOf(slot) is not { } asset) return false;
		OpenPaint(PixelsToPaint(slot, asset), slot);
		return true;
	}

	public void ClosePaint()
	{
		FinishPaintTool();
		Paint.Close();
		PaintSlot = null;
		PaintFollowed = null;
	}

	// What following the playhead asks of the editor: nothing; to close (its drawing is gone); or
	// to show the part's drawing here, opened already (Fit: another size, so zoom to fit it)
	public enum PaintFollow { Stay, Close, Opened, OpenedNewSize }

	// Stepped or scrubbed to a frame where the part shows another drawing: the editor switches to
	// it, so you can draw frame by frame. Not while playing, and never mid-stroke (busy).
	public PaintFollow FollowPaint(bool busy)
	{
		if (Paint.Asset is not { } open) return PaintFollow.Stay;

		bool gone = !Rig.Assets.ContainsKey(open.Id);   // deleted, or undone out of the project
		if (PaintSlot is { } part && !Rig.Slots.Contains(part)) PaintSlot = null;
		if (PaintSlot is not { } slot) return gone ? PaintFollow.Close : PaintFollow.Stay;
		if (busy || (State.Playing && !gone)) return PaintFollow.Stay;

		var shown = PictureOf(slot);   // what the part shows in the current pose
		if (shown == null) return gone ? PaintFollow.Close : PaintFollow.Stay;   // hidden here (and the old drawing gone?)
		if (shown == PaintFollowed && !gone) return PaintFollow.Stay;

		PaintFollowed = shown;
		if (shown == open) return PaintFollow.Stay;
		OpenPaint(shown, slot);
		return shown.Width != open.Width || shown.Height != open.Height ? PaintFollow.OpenedNewSize : PaintFollow.Opened;
	}
}
