using System.Numerics;
using Animator.Core.Editing;
using Animator.Core.Keyframing;
using SkiaSharp;

namespace Animator.Viewer.Panels;

public class TimelinePanel
{
    private readonly EditorState _state;

    private const float LabelWidth = 150f;
    private const float RulerHeight = 24f;
    private const float RowHeight = 20f;
    private const float LeftPad = 10f;
    private const float KeyPickPx = 7f;

    private float _frameWidth = 10f;
    private float _scroll;          // how far the rows are scrolled up, in pixels
    private float _viewHeight;

    private enum Drag { None, Scrub, Keys }
    private Drag _drag;

    // Key dragging
    private int _dragStartFrame;
    private int _dragDelta;
    private AnimationSnapshot? _dragBefore;
    private List<(Timeline Tl, int Frame)> _dragKeys = new();

    // Copy/paste: frames are stored relative to the earliest copied key
    private readonly List<(Timeline Tl, int RelFrame, object Key)> _clipboard = new();

    public TimelinePanel(EditorState state) => _state = state;

    private HashSet<(Timeline Tl, int Frame)> Sel => _state.SelectedKeys;

    private float FrameToX(int frame) => LabelWidth + LeftPad + frame * _frameWidth;
    private int XToFrame(float x) => (int)MathF.Round((x - LabelWidth - LeftPad) / _frameWidth);
    private float FrameToTime(int frame) => frame / (float)_state.Fps;
    private int TimeToFrame(float time) => (int)MathF.Round(time * _state.Fps);

    private float RowTop(int row) => RulerHeight + row * RowHeight - _scroll;
    private int RowAt(float y) => (int)MathF.Floor((y - RulerHeight + _scroll) / RowHeight);

    // The frame under an x position, kept inside the animation
    public int FrameAt(float x) => Math.Clamp(XToFrame(x), 0, _state.FrameCount);

    // Over the keys (not the row names or the ruler)
    public bool IsInKeyArea(Vector2 p) => p.X >= LabelWidth && p.Y >= RulerHeight;

    // ======================= drawing =======================

    public void Draw(SKCanvas canvas, Vector2 size)
    {
        _viewHeight = size.Y;
        ClampScroll();

        int frames = Math.Max(1, _state.FrameCount);
        _frameWidth = Math.Max(4f, (size.X - LabelWidth - LeftPad * 2) / frames);

        canvas.Clear(new SKColor(30, 33, 39));

        using var font = new SKFont { Size = 12 };
        using var smallFont = new SKFont { Size = 11 };
        using var text = new SKPaint { Color = new SKColor(200, 200, 200), IsAntialias = true };
        using var eventText = new SKPaint { Color = new SKColor(140, 230, 160), IsAntialias = true };
        using var tick = new SKPaint { Color = new SKColor(255, 255, 255, 90), StrokeWidth = 1 };
        using var divider = new SKPaint { Color = new SKColor(255, 255, 255, 30), StrokeWidth = 1 };
        using var segment = new SKPaint { Color = new SKColor(255, 200, 60, 70), StrokeWidth = 2 };
        using var rowSel = new SKPaint { Color = new SKColor(80, 220, 255, 35) };
        using var key = new SKPaint { Color = new SKColor(255, 200, 60), IsAntialias = true };
        using var keyRow = new SKPaint { Color = new SKColor(80, 220, 255), IsAntialias = true };
        using var keyPicked = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var spaceKey = new SKPaint { Color = new SKColor(190, 130, 255), IsAntialias = true };
        using var eventKey = new SKPaint { Color = new SKColor(120, 220, 140), IsAntialias = true, StrokeWidth = 1.5f };
        using var playhead = new SKPaint { Color = new SKColor(255, 90, 90), StrokeWidth = 2 };
        using var scrollbar = new SKPaint { Color = new SKColor(255, 255, 255, 50), IsAntialias = true };

        if (_state.AnimateMode)
            canvas.DrawRect(0, 0, size.X, 3, playhead);

        // Ruler
        for (int f = 0; f <= frames; f++)
        {
            float x = FrameToX(f);
            float h = f % 5 == 0 ? 10 : 4;
            canvas.DrawLine(x, RulerHeight - h, x, RulerHeight, tick);
            if (f % 10 == 0)
                canvas.DrawText(f.ToString(), x + 2, 14, SKTextAlign.Left, font, text);
        }
        canvas.DrawLine(0, RulerHeight, size.X, RulerHeight, divider);

        // Rows, scrolled, and clipped so they never cover the ruler
        canvas.Save();
        canvas.ClipRect(new SKRect(0, RulerHeight, size.X, size.Y));

        var timelines = _state.Animation.Timelines;
        for (int i = 0; i < timelines.Count; i++)
        {
            float y = RowTop(i);
            if (y + RowHeight < RulerHeight || y > size.Y) continue;   // scrolled out of view

            var tl = timelines[i];
            float cy = y + RowHeight / 2;
            // The selected part's rows, or else the selected bone's (a part also selects its bone)
            var part = _state.SelectedSlot;
            bool boneRow = part == null && _state.Selected != null && RigOps.TargetsBone(tl) && tl.Target == _state.Selected.Name;
            bool partRow = part != null && PartOps.TargetsPart(tl) && tl.Target == part.Name;
            bool mine = boneRow || partRow;

            if (mine) canvas.DrawRect(0, y, size.X, RowHeight, rowSel);
            canvas.DrawLine(0, y + RowHeight, size.X, y + RowHeight, divider);

            canvas.Save();
            canvas.ClipRect(new SKRect(0, y, LabelWidth - 4, y + RowHeight));
            canvas.DrawText(tl.Label, 8, y + 14, SKTextAlign.Left, font, tl is EventTimeline ? eventText : text);
            canvas.Restore();

            if (tl is EventTimeline events)
            {
                // Events: a flag per marker, with its name beside it
                foreach (var k in events.Track.Keys)
                {
                    int f = TimeToFrame(k.Time);
                    float x = FrameToX(f);
                    DrawFlag(canvas, x, cy, Sel.Contains((tl, f)) ? keyPicked : eventKey);
                    canvas.DrawText(k.Value.Name, x + 7, y + 14, SKTextAlign.Left, smallFont, eventText);
                }
                continue;
            }

            if (tl is AttachmentTimeline drawings)
                DrawExposures(canvas, drawings, y, frames, smallFont);

            var keys = tl.KeyInfos.ToList();

            // Faint line = "this segment interpolates". No line = stepped (holds).
            for (int k = 0; k < keys.Count - 1; k++)
                if (keys[k].Curve != CurveType.Stepped)
                    canvas.DrawLine(FrameToX(TimeToFrame(keys[k].Time)), cy,
                                    FrameToX(TimeToFrame(keys[k + 1].Time)), cy, segment);

            foreach (var (t, curve) in keys)
            {
                int f = TimeToFrame(t);
                var paint = Sel.Contains((tl, f)) ? keyPicked
                          : tl is SpaceTimeline ? spaceKey
                          : mine ? keyRow
                          : key;
                DrawKey(canvas, FrameToX(f), cy, curve, paint);
            }
        }

        // A thin scrollbar when there are more rows than fit
        float content = timelines.Count * RowHeight;
        float visible = size.Y - RulerHeight;
        if (content > visible && visible > 0)
        {
            float barHeight = MathF.Max(16, visible * visible / content);
            float barTop = RulerHeight + (visible - barHeight) * (_scroll / (content - visible));
            canvas.DrawRoundRect(size.X - 6, barTop, 4, barHeight, 2, 2, scrollbar);
        }

        canvas.Restore();

        canvas.DrawLine(LabelWidth, 0, LabelWidth, size.Y, divider);

        float px = FrameToX(_state.Frame);
        canvas.DrawLine(px, 0, px, size.Y, playhead);
        canvas.DrawText($"frame {_state.Frame} / {frames}", 8, 14, SKTextAlign.Left, font, text);
    }

    // Shape shows the curve: diamond = Linear, circle = Smooth, square = Stepped
    private static void DrawKey(SKCanvas canvas, float x, float y, CurveType curve, SKPaint paint)
    {
        switch (curve)
        {
            case CurveType.Stepped:
                canvas.DrawRect(x - 4, y - 4, 8, 8, paint);
                break;
            case CurveType.Smooth:
                canvas.DrawCircle(x, y, 4.5f, paint);
                break;
            default:
                // A diamond is just a square rotated 45 degrees
                canvas.Save();
                canvas.Translate(x, y);
                canvas.RotateDegrees(45);
                canvas.DrawRect(-3.5f, -3.5f, 7, 7, paint);
                canvas.Restore();
                break;
        }
    }

    // A part's drawing row: every drawing is a block from its key to the next key, with its
    // name inside. The same drawing always gets the same color, so repeats stand out (lip sync).
    private void DrawExposures(SKCanvas canvas, AttachmentTimeline row, float y, int frames, SKFont font)
    {
        var keys = row.Track.Keys;
        float top = y + 3, height = RowHeight - 6;

        using var fill = new SKPaint { IsAntialias = true };
        using var outline = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true
        };
        using var label = new SKPaint { Color = new SKColor(240, 240, 240), IsAntialias = true };
        using var faintLabel = new SKPaint { Color = new SKColor(240, 240, 240, 110), IsAntialias = true };

        void Block(int from, int to, string? drawing, bool faint)
        {
            float x0 = FrameToX(from), x1 = FrameToX(to);
            if (x1 - x0 < 1) return;
            var rect = new SKRect(x0, top, x1, top + height);
            if (drawing == null)
            {
                canvas.DrawRoundRect(rect, 3, 3, outline);   // hidden: an empty outline
                return;
            }

            fill.Color = SKColor.FromHsl(HueOf(drawing), 45, 38).WithAlpha(faint ? (byte)60 : (byte)170);
            canvas.DrawRoundRect(rect, 3, 3, fill);
            canvas.Save();
            canvas.ClipRect(rect);
            canvas.DrawText(drawing, x0 + 7, top + height - 4, SKTextAlign.Left, font, faint ? faintLabel : label);
            canvas.Restore();
        }

        // Before the row's first key, the part shows its rest drawing: drawn faint
        int first = keys.Count > 0 ? TimeToFrame(keys[0].Time) : frames;
        if (first > 0 && _state.Skeleton.FindSlot(row.Target) is { } slot)
            Block(0, Math.Min(first, frames), slot.SetupAttachment, faint: true);

        for (int k = 0; k < keys.Count; k++)
        {
            int from = TimeToFrame(keys[k].Time);
            if (from >= frames) break;
            int to = k + 1 < keys.Count ? TimeToFrame(keys[k + 1].Time) : frames;
            Block(from, Math.Min(to, frames), keys[k].Value, faint: false);
        }
    }

    // The same color for the same drawing name, every time (FNV-1a hash → hue)
    private static float HueOf(string name)
    {
        uint hash = 2166136261;
        foreach (char c in name)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash % 360;
    }

    // A little flag: a pole with a pennant at the top
    private static void DrawFlag(SKCanvas canvas, float x, float y, SKPaint paint)
    {
        canvas.DrawLine(x, y - 7, x, y + 7, paint);
        canvas.DrawRect(x, y - 7, 6, 5, paint);
    }

    // ======================= scrolling =======================

    public void Wheel(int delta)
    {
        _scroll -= delta / 120f * RowHeight * 2;   // one wheel notch = two rows
        ClampScroll();
    }

    private void ClampScroll()
    {
        float content = _state.Animation.Timelines.Count * RowHeight;
        float visible = MathF.Max(0, _viewHeight - RulerHeight);
        _scroll = Math.Clamp(_scroll, 0, MathF.Max(0, content - visible));
    }

    // ======================= mouse =======================

    public void MouseDown(Vector2 p, bool shift)
    {
        // Clicking a row's name selects what that row animates (a bone, or a part)
        if (p.X < LabelWidth && p.Y > RulerHeight)
        {
            int row = RowAt(p.Y);
            var timelines = _state.Animation.Timelines;
            if (row >= 0 && row < timelines.Count)
            {
                switch (timelines[row])
                {
                    case var t when PartOps.TargetsPart(t) && _state.Skeleton.FindSlot(t.Target) is { } slot:
                        _state.SelectedSlot = slot;
                        _state.Selected = slot.Bone;
                        break;
                    case var t when RigOps.TargetsBone(t):
                        _state.SelectedSlot = null;
                        _state.Selected = _state.Skeleton.FindBone(t.Target);
                        break;
                }
            }
            return;
        }

        if (HitKey(p) is { } hit)
        {
            if (shift)
            {
                if (!Sel.Remove(hit)) Sel.Add(hit);   // toggle
                if (!Sel.Contains(hit)) return;       // just deselected: don't start a drag
            }
            else if (!Sel.Contains(hit))
            {
                Sel.Clear();
                Sel.Add(hit);
            }

            _drag = Drag.Keys;
            _dragStartFrame = hit.Frame;
            _dragDelta = 0;
            _dragKeys = Sel.ToList();
            _dragBefore = AnimationSnapshot.Capture(_state.Animation);

            _state.Playing = false;
            _state.SetFrame(hit.Frame);   // jump to the clicked key so you see its pose
            return;
        }

        if (!shift) Sel.Clear();
        _drag = Drag.Scrub;
        Scrub(p.X);
    }

    public void MouseMove(Vector2 p)
    {
        if (_drag == Drag.Scrub)
        {
            Scrub(p.X);
        }
        else if (_drag == Drag.Keys && _dragBefore != null)
        {
            int delta = XToFrame(p.X) - _dragStartFrame;

            // Keep every dragged key inside the animation's frame range
            int min = _dragKeys.Min(k => k.Frame);
            int max = _dragKeys.Max(k => k.Frame);
            delta = Math.Clamp(delta, -min, _state.FrameCount - max);

            if (delta == _dragDelta) return;
            _dragDelta = delta;

            _dragBefore.Restore();   // back to the original keys...
            MoveKeys(delta);         // ...then apply the whole move again
        }
    }

    public void MouseUp()
    {
        if (_drag == Drag.Keys && _dragBefore != null && _dragDelta != 0)
        {
            _state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
                $"Move {_dragKeys.Count} key(s) by {_dragDelta} frames",
                _dragBefore, AnimationSnapshot.Capture(_state.Animation), s => s.Restore()));
        }
        _drag = Drag.None;
        _dragBefore = null;
    }

    private void Scrub(float x)
    {
        _state.Playing = false;
        _state.SetFrame(XToFrame(x));
    }

    private void MoveKeys(int delta)
    {
        // Take ALL keys out first, then put them back. Moving one at a time
        // could drop a key onto a spot another selected key hasn't left yet.
        var taken = _dragKeys
            .Select(k => (k.Tl, k.Frame, Key: k.Tl.TakeKey(FrameToTime(k.Frame))))
            .ToList();

        Sel.Clear();
        foreach (var (tl, frame, key) in taken)
        {
            if (key == null) continue;
            tl.PutKey(key, FrameToTime(frame + delta));
            Sel.Add((tl, frame + delta));
        }
        _state.SetFrame(_dragStartFrame + delta);
    }

    private (Timeline Tl, int Frame)? HitKey(Vector2 p)
    {
        if (p.X < LabelWidth || p.Y < RulerHeight) return null;

        int row = RowAt(p.Y);
        var timelines = _state.Animation.Timelines;
        if (row < 0 || row >= timelines.Count) return null;

        var tl = timelines[row];
        (Timeline Tl, int Frame)? best = null;
        float bestDist = KeyPickPx;
        foreach (var (t, _) in tl.KeyInfos)
        {
            int f = TimeToFrame(t);
            float d = MathF.Abs(FrameToX(f) - p.X);
            if (d <= bestDist) { best = (tl, f); bestDist = d; }
        }
        return best;
    }

    // ======================= events =======================

    // The event marker under the mouse, if any
    public (EventTimeline Events, int Frame)? EventAt(Vector2 p) =>
        HitKey(p) is { Tl: EventTimeline events } hit ? (events, hit.Frame) : null;

    // True when every selected key is an event marker (the right-click menu differs)
    public bool SelectionIsEvents => Sel.Count > 0 && Sel.All(k => k.Tl is EventTimeline);

    // The one selected event marker, if exactly one is selected
    public (EventTimeline Events, int Frame)? SingleSelectedEvent =>
        Sel.Count == 1 && Sel.First() is { Tl: EventTimeline events } only ? (events, only.Frame) : null;

    // ======================= key operations (all undoable) =======================

    private void Edit(string name, Action change)
    {
        var before = AnimationSnapshot.Capture(_state.Animation);
        change();
        var after = AnimationSnapshot.Capture(_state.Animation);
        _state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(name, before, after, s => s.Restore()));
    }

    // Right-click: make sure the clicked key is part of the selection
    public bool SelectForMenu(Vector2 p)
    {
        if (HitKey(p) is not { } hit) return false;
        if (!Sel.Contains(hit))
        {
            Sel.Clear();
            Sel.Add(hit);
        }
        return true;
    }

    // The curve shared by all selected keys, or null if they differ
    public CurveType? SelectedCurve()
    {
        CurveType? result = null;
        foreach (var (tl, f) in Sel)
        {
            foreach (var (t, c) in tl.KeyInfos)
            {
                if (TimeToFrame(t) != f) continue;
                if (result == null) result = c;
                else if (result != c) return null;
            }
        }
        return result;
    }

    public void SetCurve(CurveType curve)
    {
        if (Sel.Count == 0) return;
        Edit($"Set {Sel.Count} key(s) to {curve}", () =>
        {
            foreach (var (tl, f) in Sel) tl.SetCurve(FrameToTime(f), curve);
        });
    }

    public bool DeleteSelected()
    {
        if (Sel.Count == 0) return false;
        Edit($"Delete {Sel.Count} key(s)", () =>
        {
            foreach (var (tl, f) in Sel) tl.RemoveKey(FrameToTime(f));
        });
        Sel.Clear();
        return true;
    }

    public void Copy()
    {
        if (Sel.Count == 0) return;
        int min = Sel.Min(k => k.Frame);
        _clipboard.Clear();
        foreach (var (tl, f) in Sel)
        {
            var key = tl.CopyKey(FrameToTime(f));
            if (key != null) _clipboard.Add((tl, f - min, key));
        }
    }

    // Pastes into the same timelines, with the earliest key landing on the playhead
    public void Paste()
    {
        if (_clipboard.Count == 0) return;
        int at = _state.Frame;
        var pasted = new List<(Timeline, int)>();

        Edit($"Paste {_clipboard.Count} key(s) at frame {at}", () =>
        {
            foreach (var (tl, rel, key) in _clipboard)
            {
                int f = at + rel;
                // Skip keys past the end, or timelines that were undone out of existence
                if (f > _state.FrameCount || !_state.Animation.Timelines.Contains(tl)) continue;
                tl.PutKey(key, FrameToTime(f));
                pasted.Add((tl, f));
            }
        });

        Sel.Clear();
        foreach (var k in pasted) Sel.Add(k);
    }
}
