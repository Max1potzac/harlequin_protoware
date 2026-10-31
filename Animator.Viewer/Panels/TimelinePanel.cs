using System.Numerics;
using System.Windows.Input;
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
    private object? _scrolledTo;    // the bone or part whose rows were last brought into view
    private float _viewHeight;

    private enum Drag { None, Scrub, Keys, Box }
    private Drag _drag;

    // Ctrl+drag over the keys: a box picks the keys in it (with Shift, as well as the ones picked)
    private Vector2 _boxFrom, _boxTo;
    private HashSet<(Timeline Tl, int Frame)> _boxKept = new();

    // Only the rows of what's picked (and the events and stacking-order rows) when something is
    public bool OnlyPicked { get; set; }

    // The key that keys unkeyed changes (auto key off), for the note about them
    public string KeyHint { get; set; } = "S";

    // Key dragging
    private int _dragStartFrame;
    private int _dragDelta;
    private AnimationSnapshot? _dragBefore;
    private List<(Timeline Tl, int Frame)> _dragKeys = new();

    // Copy/paste: frames are stored relative to the earliest copied key
    private readonly List<(Timeline Tl, int RelFrame, object Key)> _clipboard = new();

    // A run of frames picked by dragging across the keys, as in a video editor (the playhead goes
    // along): right-click it to split it off, or copy it, into an animation sequence of its own
    public (int First, int Last)? Range { get; private set; }
    private int _rangeAnchor;
    private bool _picking;   // this drag picks frames (it started over the keys, not the ruler)

    public void ClearRange() => Range = null;

    public TimelinePanel(EditorState state) => _state = state;

    // The rows shown, top to bottom
    private IReadOnlyList<Timeline> Rows()
    {
        var all = _state.Animation.Timelines;
        if (!OnlyPicked || _state.Picked.Count == 0) return all;
        var bones = _state.PickedBones.Select(b => b.Name).ToHashSet();
        var parts = _state.PickedSlots.Select(s => s.Name).ToHashSet();
        return all.Where(tl => tl is EventTimeline or DrawOrderTimeline or ColorLayerTimeline or LayerAdjustTimeline or HitboxTimeline ||
                               (RigOps.TargetsBone(tl) && bones.Contains(tl.Target)) ||
                               (PartOps.TargetsPart(tl) && parts.Contains(tl.Target))).ToList();
    }

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
        if (!ReferenceEquals(_state.Primary, _scrolledTo))
        {
            _scrolledTo = _state.Primary;
            ShowRowsOf(_scrolledTo);   // something else picked: its rows, if they're out of sight
        }

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
        using var orderKey = new SKPaint { Color = new SKColor(90, 210, 220), IsAntialias = true };
        using var orderText = new SKPaint { Color = new SKColor(130, 215, 225), IsAntialias = true };
        using var playhead = new SKPaint { Color = new SKColor(255, 90, 90), StrokeWidth = 2 };
        using var scrollbar = new SKPaint { Color = new SKColor(255, 255, 255, 50), IsAntialias = true };

        if (_state.AnimateMode)
        {
            using var noAutoKey = new SKPaint { Color = new SKColor(255, 170, 40) };
            canvas.DrawRect(0, 0, size.X, 3, _state.AutoKey ? playhead : noAutoKey);   // orange: auto key off
        }

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

        // The rows of everything picked stand out, keys and all (a picked part: its own rows, not its bone's)
        var pickedBones = _state.PickedBones.Select(b => b.Name).ToHashSet();
        var pickedParts = _state.PickedSlots.Select(s => s.Name).ToHashSet();

        var timelines = Rows();
        using var held = new SKPaint { Color = new SKColor(255, 170, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        for (int i = 0; i < timelines.Count; i++)
        {
            float y = RowTop(i);
            if (y + RowHeight < RulerHeight || y > size.Y) continue;   // scrolled out of view

            var tl = timelines[i];
            float cy = y + RowHeight / 2;
            bool mine = (RigOps.TargetsBone(tl) && pickedBones.Contains(tl.Target)) ||
                        (PartOps.TargetsPart(tl) && pickedParts.Contains(tl.Target));

            if (mine) canvas.DrawRect(0, y, size.X, RowHeight, rowSel);
            canvas.DrawLine(0, y + RowHeight, size.X, y + RowHeight, divider);

            canvas.Save();
            canvas.ClipRect(new SKRect(0, y, LabelWidth - 4, y + RowHeight));
            canvas.DrawText(tl.Label, 8, y + 14, SKTextAlign.Left, font, tl is EventTimeline ? eventText : tl is DrawOrderTimeline ? orderText : text);
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
                          : tl is DrawOrderTimeline ? orderKey
                          : mine ? keyRow
                          : key;
                DrawKey(canvas, FrameToX(f), cy, curve, paint);
            }

            // Unkeyed changes here (auto key off): an empty key at the playhead, where S would put one
            if (_state.AnimateMode && _state.Hold.Any && Animated(tl) is { } posed && _state.Hold.Holds(posed))
                DrawKey(canvas, FrameToX(_state.Frame), cy, CurveType.Linear, held);

            // Colour keys get a dot of their colour in the middle
            if (tl is PartColorTimeline colors)
            {
                using var dot = new SKPaint { IsAntialias = true };
                foreach (var k in colors.Track.Keys)
                {
                    dot.Color = new SKColor(Byte(k.Value.X), Byte(k.Value.Y), Byte(k.Value.Z));
                    canvas.DrawCircle(FrameToX(TimeToFrame(k.Time)), cy, 2.5f, dot);
                }
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

        if (Range is { } range) DrawRange(canvas, size, range, font);

        if (_drag == Drag.Box)
        {
            using var boxFill = new SKPaint { Color = new SKColor(255, 255, 255, 25) };
            using var boxEdge = new SKPaint { Color = new SKColor(255, 255, 255, 140), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            var box = BoxRect();
            canvas.DrawRect(box, boxFill);
            canvas.DrawRect(box, boxEdge);
        }

        float px = FrameToX(_state.Frame);
        canvas.DrawLine(px, 0, px, size.Y, playhead);
        canvas.DrawText($"frame {_state.Frame} / {frames}", 8, 14, SKTextAlign.Left, font, text);

        if (_state.AnimateMode && _state.Hold.Any)
        {
            using var note = new SKPaint { Color = new SKColor(255, 180, 70), IsAntialias = true };
            int count = _state.Hold.Count;
            canvas.DrawText($"{count} unkeyed change{(count == 1 ? "" : "s")}: {KeyHint} keys {(count == 1 ? "it" : "them")}, another frame drops {(count == 1 ? "it" : "them")}",
                size.X - 8, 14, SKTextAlign.Right, font, note);
        }
    }

    private SKRect BoxRect() => new(MathF.Min(_boxFrom.X, _boxTo.X), MathF.Min(_boxFrom.Y, _boxTo.Y),
                                    MathF.Max(_boxFrom.X, _boxTo.X), MathF.Max(_boxFrom.Y, _boxTo.Y));

    // The keys of the shown rows inside the box (a row counts if the box reaches into it)
    private IEnumerable<(Timeline Tl, int Frame)> KeysInBox()
    {
        var box = BoxRect();
        var rows = Rows();
        for (int i = 0; i < rows.Count; i++)
        {
            float top = RowTop(i);
            if (top + RowHeight < box.Top || top > box.Bottom) continue;
            foreach (var t in rows[i].KeyTimes)
            {
                int f = TimeToFrame(t);
                float x = FrameToX(f);
                if (x >= box.Left - 3 && x <= box.Right + 3) yield return (rows[i], f);
            }
        }
    }

    // The picked frames: a band over their keys, and across the ruler with how many there are
    private void DrawRange(SKCanvas canvas, Vector2 size, (int First, int Last) range, SKFont font)
    {
        float x0 = MathF.Max(LabelWidth, FrameToX(range.First) - _frameWidth / 2);
        float x1 = MathF.Min(size.X, FrameToX(range.Last) + _frameWidth / 2);
        if (x1 <= x0) return;
        using var band = new SKPaint { Color = new SKColor(80, 160, 255, 45) };
        using var top = new SKPaint { Color = new SKColor(80, 160, 255, 150) };
        using var edge = new SKPaint { Color = new SKColor(120, 190, 255, 200), StrokeWidth = 1 };
        using var label = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawRect(x0, RulerHeight, x1 - x0, size.Y - RulerHeight, band);
        canvas.DrawRect(x0, RulerHeight - 6, x1 - x0, 6, top);
        canvas.DrawLine(x0, 0, x0, size.Y, edge);
        canvas.DrawLine(x1, 0, x1, size.Y, edge);
        int count = range.Last - range.First + 1;
        canvas.DrawText($"{range.First}–{range.Last} ({count} frames)", x0 + 4, RulerHeight - 9, SKTextAlign.Left, font, label);
    }

    // Shape shows the curve: diamond = Linear, circle = Smooth, square = Stepped, ring = Bézier
    public static void DrawKey(SKCanvas canvas, float x, float y, CurveType curve, SKPaint paint)
    {
        switch (curve)
        {
            case CurveType.Bezier:
                {
                    using var ring = new SKPaint { Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f };
                    canvas.DrawCircle(x, y, 3.8f, ring);
                    break;
                }
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

    private static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);

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

    // Scrolls the first row of this bone or part into view, when none of its rows can be seen
    private void ShowRowsOf(object? item)
    {
        if (item == null) return;
        var timelines = Rows();
        var rows = Enumerable.Range(0, timelines.Count).Where(i => Animated(timelines[i]) == item).ToList();
        if (rows.Count == 0) return;
        float visible = MathF.Max(0, _viewHeight - RulerHeight);
        bool seen = rows.Any(i => RowTop(i) >= RulerHeight && RowTop(i) + RowHeight <= RulerHeight + visible);
        if (seen) return;
        _scroll = rows[0] * RowHeight - MathF.Max(0, (visible - RowHeight * Math.Min(rows.Count, 4)) / 2);
        ClampScroll();
    }

    private void ClampScroll()
    {
        float content = Rows().Count * RowHeight;
        float visible = MathF.Max(0, _viewHeight - RulerHeight);
        _scroll = Math.Clamp(_scroll, 0, MathF.Max(0, content - visible));
    }

    // ======================= mouse =======================

    public void MouseDown(Vector2 p, ModifierKeys modifiers)
    {
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);

        // Clicking a row's name picks what that row animates, a bone or a part (Ctrl: picked or
        // not, the other way round; Shift: picked too)
        if (p.X < LabelWidth && p.Y > RulerHeight)
        {
            int row = RowAt(p.Y);
            var timelines = Rows();
            if (row >= 0 && row < timelines.Count && Animated(timelines[row]) is { } item)
            {
                if (modifiers.HasFlag(ModifierKeys.Control)) _state.TogglePick(item);
                else if (shift) _state.PickMore(new[] { item });
                else _state.Pick(new[] { item });
            }
            return;
        }

        if (HitKey(p) is { } hit)
        {
            if (modifiers.HasFlag(ModifierKeys.Alt))
            {
                // Alt: every key at that frame (Shift: as well as the ones picked)
                if (!shift) Sel.Clear();
                foreach (var key in KeysAt(hit.Frame)) Sel.Add(key);
            }
            else if (shift)
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

        // Ctrl: a box over the keys picks the keys in it
        if (modifiers.HasFlag(ModifierKeys.Control) && IsInKeyArea(p))
        {
            _drag = Drag.Box;
            _boxFrom = _boxTo = p;
            if (!shift) Sel.Clear();
            _boxKept = Sel.ToHashSet();
            return;
        }

        if (!shift) Sel.Clear();
        _drag = Drag.Scrub;
        Scrub(p.X);

        // Over the keys, a drag picks the frames it crosses (a click lets them go); the ruler just scrubs
        _picking = p.Y >= RulerHeight;
        if (_picking)
        {
            _rangeAnchor = LastFrameAt(p.X);
            Range = null;
        }
    }

    // The bone or part a row animates (null: another kind of row, or it's gone)
    private object? Animated(Timeline row) => row switch
    {
        ColorLayerTimeline colour => _state.Skeleton.FindColorLayer(colour.Target),
        LayerAdjustTimeline layer => ColorLayerOps.FindAssetLayer(_state.Skeleton, layer.Target)?.Layer,
        _ when PartOps.TargetsPart(row) => _state.Skeleton.FindSlot(row.Target),
        _ when RigOps.TargetsBone(row) => _state.Skeleton.FindBone(row.Target),
        _ => null
    };

    // Every key of these bones and parts (a part: its own rows) becomes the selection; how many
    public int SelectKeysOf(IEnumerable<Bone> bones, IEnumerable<Slot> parts)
    {
        var boneNames = bones.Select(b => b.Name).ToHashSet();
        var partNames = parts.Select(s => s.Name).ToHashSet();
        Sel.Clear();
        foreach (var tl in _state.Animation.Timelines)
        {
            bool mine = (RigOps.TargetsBone(tl) && boneNames.Contains(tl.Target)) ||
                        (PartOps.TargetsPart(tl) && partNames.Contains(tl.Target));
            if (!mine) continue;
            foreach (var time in tl.KeyTimes) Sel.Add((tl, TimeToFrame(time)));
        }
        return Sel.Count;
    }

    // A frame of the animation under an x position (not the end, where it loops)
    private int LastFrameAt(float x) => Math.Clamp(XToFrame(x), 0, Math.Max(0, _state.FrameCount - 1));

    public void MouseMove(Vector2 p)
    {
        if (_drag == Drag.Scrub)
        {
            Scrub(p.X);
            if (_picking)
            {
                int frame = LastFrameAt(p.X);
                Range = frame == _rangeAnchor ? null : (Math.Min(frame, _rangeAnchor), Math.Max(frame, _rangeAnchor));
            }
        }
        else if (_drag == Drag.Box)
        {
            _boxTo = p;
            Sel.Clear();
            Sel.UnionWith(_boxKept);
            Sel.UnionWith(KeysInBox());
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
        _picking = false;
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
        var timelines = Rows();
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

    // The selected keys eased by these Bézier handles (from the key to the next)
    public void SetBezier(Bezier bezier, string name)
    {
        if (Sel.Count == 0) return;
        Edit($"{Sel.Count} key(s): {name}", () =>
        {
            foreach (var (tl, f) in Sel) tl.SetBezier(FrameToTime(f), bezier);
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

    // ======================= the dopesheet's tools (all undoable) =======================

    // Every key of the shown rows at a frame
    public List<(Timeline Tl, int Frame)> KeysAt(int frame) =>
        Rows().SelectMany(tl => tl.KeyTimes.Select(t => (Tl: tl, Frame: TimeToFrame(t)))).Where(k => k.Frame == frame).ToList();

    // Picks every key of the shown rows at this frame, or from it on
    public int SelectKeys(int frame, bool fromOn)
    {
        Sel.Clear();
        foreach (var tl in Rows())
            foreach (var t in tl.KeyTimes)
            {
                int f = TimeToFrame(t);
                if (fromOn ? f >= frame : f == frame) Sel.Add((tl, f));
            }
        return Sel.Count;
    }

    private void Reselect(IEnumerable<(Timeline Tl, int Frame)> keys)
    {
        Sel.Clear();
        Sel.UnionWith(keys);
    }

    // The selected keys some frames earlier (negative) or later
    public bool Nudge(int by)
    {
        if (Sel.Count == 0 || by == 0) return false;
        var keys = Sel.ToList();
        Edit($"Nudge {keys.Count} key(s) {(by < 0 ? "earlier" : "later")}", () => Reselect(KeyTimeOps.Nudge(keys, by, _state.FrameCount, _state.Fps)));
        return true;
    }

    // Further apart or closer together (factor 2 = twice as long), from the first selected key
    public bool Stretch(float factor)
    {
        if (Sel.Count < 2 || !(factor > 0)) return false;
        var keys = Sel.ToList();
        int from = keys.Min(k => k.Frame);
        Edit($"Stretch {keys.Count} keys to {factor * 100:0}%", () => Reselect(KeyTimeOps.Stretch(keys, from, factor, _state.FrameCount, _state.Fps)));
        return true;
    }

    // Back to front, between the first and last selected key
    public bool ReverseSelected()
    {
        if (Sel.Count < 2) return false;
        var keys = Sel.ToList();
        Edit($"Reverse {keys.Count} keys", () => Reselect(KeyTimeOps.Reverse(keys, _state.Fps)));
        return true;
    }

    // New frames after `at` (every later key moves on, the animation gets longer)
    public void InsertFrames(int at, int count)
    {
        if (count <= 0) return;
        Sel.Clear();
        Range = null;
        Edit(count == 1 ? $"Insert a frame after frame {at}" : $"Insert {count} frames after frame {at}",
            () => KeyTimeOps.InsertFrames(_state.Animation, at, count, _state.Fps));
    }

    // Frames at..at+count-1 taken out (their keys go, later keys move up). false: nothing to take
    public bool RemoveFrames(int at, int count)
    {
        var before = AnimationSnapshot.Capture(_state.Animation);
        int removed = KeyTimeOps.RemoveFrames(_state.Animation, at, count, _state.Fps);
        if (removed == 0) return false;
        Sel.Clear();
        Range = null;
        _state.Undo.Push(new SnapshotCommand<AnimationSnapshot>(
            removed == 1 ? $"Remove frame {at}" : $"Remove frames {at}–{at + removed - 1}",
            before, AnimationSnapshot.Capture(_state.Animation), s => s.Restore()));
        _state.SetFrame(Math.Min(at, _state.FrameCount));
        return true;
    }

    // The playhead to the next key of the shown rows after it (or the one before it). false: none
    public bool JumpToKey(int direction)
    {
        int now = _state.Frame;
        var frames = Rows().SelectMany(tl => tl.KeyTimes).Select(TimeToFrame).Distinct().ToList();
        var next = direction > 0 ? frames.Where(f => f > now).DefaultIfEmpty(-1).Min()
                                 : frames.Where(f => f < now).DefaultIfEmpty(-1).Max();
        if (next < 0) return false;
        _state.Playing = false;
        _state.SetFrame(next);
        return true;
    }
}
