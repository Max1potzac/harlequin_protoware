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

    // ======================= drawing =======================

    public void Draw(SKCanvas canvas, Vector2 size)
    {
        int frames = Math.Max(1, _state.FrameCount);
        _frameWidth = Math.Max(4f, (size.X - LabelWidth - LeftPad * 2) / frames);

        canvas.Clear(new SKColor(30, 33, 39));

        using var font = new SKFont { Size = 12 };
        using var text = new SKPaint { Color = new SKColor(200, 200, 200), IsAntialias = true };
        using var tick = new SKPaint { Color = new SKColor(255, 255, 255, 90), StrokeWidth = 1 };
        using var divider = new SKPaint { Color = new SKColor(255, 255, 255, 30), StrokeWidth = 1 };
        using var segment = new SKPaint { Color = new SKColor(255, 200, 60, 70), StrokeWidth = 2 };
        using var rowSel = new SKPaint { Color = new SKColor(80, 220, 255, 35) };
        using var key = new SKPaint { Color = new SKColor(255, 200, 60), IsAntialias = true };
        using var keyRow = new SKPaint { Color = new SKColor(80, 220, 255), IsAntialias = true };
        using var keyPicked = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var playhead = new SKPaint { Color = new SKColor(255, 90, 90), StrokeWidth = 2 };

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

        // Rows
        var timelines = _state.Animation.Timelines;
        for (int i = 0; i < timelines.Count; i++)
        {
            var tl = timelines[i];
            float y = RulerHeight + i * RowHeight;
            float cy = y + RowHeight / 2;
            bool boneRow = _state.Selected != null && tl.Target == _state.Selected.Name;

            if (boneRow) canvas.DrawRect(0, y, size.X, RowHeight, rowSel);
            canvas.DrawLine(0, y + RowHeight, size.X, y + RowHeight, divider);

            canvas.Save();
            canvas.ClipRect(new SKRect(0, y, LabelWidth - 4, y + RowHeight));
            canvas.DrawText(tl.Label, 8, y + 14, SKTextAlign.Left, font, text);
            canvas.Restore();

            var keys = tl.KeyInfos.ToList();

            // Faint line = "this segment interpolates". No line = stepped (holds).
            for (int k = 0; k < keys.Count - 1; k++)
                if (keys[k].Curve != CurveType.Stepped)
                    canvas.DrawLine(FrameToX(TimeToFrame(keys[k].Time)), cy,
                                    FrameToX(TimeToFrame(keys[k + 1].Time)), cy, segment);

            foreach (var (t, curve) in keys)
            {
                int f = TimeToFrame(t);
                var paint = Sel.Contains((tl, f)) ? keyPicked : boneRow ? keyRow : key;
                DrawKey(canvas, FrameToX(f), cy, curve, paint);
            }
        }

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

    // ======================= mouse =======================

    public void MouseDown(Vector2 p, bool shift)
    {
        // Clicking a row's name selects that row's bone
        if (p.X < LabelWidth && p.Y > RulerHeight)
        {
            int row = (int)((p.Y - RulerHeight) / RowHeight);
            var timelines = _state.Animation.Timelines;
            if (row < timelines.Count)
                _state.Selected = _state.Skeleton.FindBone(timelines[row].Target);
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

        int row = (int)((p.Y - RulerHeight) / RowHeight);
        var timelines = _state.Animation.Timelines;
        if (row >= timelines.Count) return null;

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