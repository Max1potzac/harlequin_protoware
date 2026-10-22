using System.Numerics;
using System.Windows.Input;
using Animator.Core;
using Animator.Core.Editing;
using Animator.Core.Rigging;
using Animator.Core.Utilities;
using Animator.Viewer.Rendering;

namespace Animator.Viewer.Tools;

public class BoneTool
{
    private enum Drag { None, Move, Rotate, Pan }

    private readonly EditorState _state;
    private readonly Camera _camera;

    private Drag _drag;
    private Vector2 _grabOffset;
    private float _lastAngle;
    private Vector2 _lastScreen;

    private PoseSnapshot? _before;   // state at mouse-down, for undo
    private bool _changed;

    private const float JointPickPx = 9f;
    private const float BonePickPx = 6f;

    public BoneTool(EditorState state, Camera camera)
    {
        _state = state;
        _camera = camera;
    }

    public void MouseDown(Vector2 screen, MouseButton button)
    {
        _lastScreen = screen;
        if (button == MouseButton.Middle) { _drag = Drag.Pan; return; }
        if (button != MouseButton.Left) return;

        var world = _camera.ScreenToWorld(screen);

        var hit = HitJoint(world);
        var mode = Drag.Move;
        if (hit == null) { hit = HitBone(world); mode = Drag.Rotate; }

        if (hit == null)
        {
            _state.Selected = null;
            _drag = Drag.Pan;
            return;
        }

        _state.Selected = hit;
        _drag = mode;
        _grabOffset = hit.World.Translation - world;
        _lastAngle = AngleFrom(hit, world);

        // Keying while the animation runs would scatter keys at random times
        if (_state.AnimateMode)
        {
            _state.Playing = false;
            _state.SetFrame(_state.Frame);   // snap to the nearest whole frame
        }

        _before = PoseSnapshot.Capture(hit, _state.Animation);
        _changed = false;
    }

    public void MouseMove(Vector2 screen)
    {
        var world = _camera.ScreenToWorld(screen);
        var bone = _state.Selected;

        switch (_drag)
        {
            case Drag.Pan:
                var d = screen - _lastScreen;
                _camera.Pan -= new Vector2(d.X, -d.Y) / _camera.Zoom;
                break;

            case Drag.Move when bone != null:
                {
                    var local = bone.WorldToParentLocal(world + _grabOffset);
                    if (!_state.AnimateMode)
                        bone.SetupPosition += local - bone.Position;
                    bone.Position = local;
                    if (_state.AnimateMode) KeyTranslate(bone);
                    _changed = true;
                    break;
                }

            case Drag.Rotate when bone != null:
                {
                    float angle = AngleFrom(bone, world);
                    float step = MathUtil.ShortestAngle(angle - _lastAngle);
                    _lastAngle = angle;
                    if (!_state.AnimateMode)
                        bone.SetupRotation += step;
                    bone.Rotation += step;
                    if (_state.AnimateMode) KeyRotate(bone);
                    _changed = true;
                    break;
                }
        }

        _lastScreen = screen;
    }

    public void MouseUp()
    {
        if (_changed && _before != null && _state.Selected is { } bone)
        {
            var after = PoseSnapshot.Capture(bone, _state.Animation);
            string verb = _drag == Drag.Move ? "Move" : "Rotate";
            string where = _state.AnimateMode ? $" (key @ frame {_state.Frame})" : "";
            _state.Undo.Push(new SnapshotCommand<PoseSnapshot>(
                $"{verb} {bone.Name}{where}", _before, after, s => s.Restore()));
        }

        _before = null;
        _changed = false;
        _drag = Drag.None;
    }

    // ---------- keying ----------
    // Keys store offsets from the setup pose (see Milestone 3)

    private void KeyTranslate(Bone b) =>
        _state.Animation.GetOrCreate(b.Name, n => new TranslateTimeline(n))
              .Track.Add(_state.Time, b.Position - b.SetupPosition);

    private void KeyRotate(Bone b) =>
        _state.Animation.GetOrCreate(b.Name, n => new RotateTimeline(n))
              .Track.Add(_state.Time, b.Rotation - b.SetupRotation);

    // ---------- hit testing ----------

    private Bone? HitJoint(Vector2 world)
    {
        float bestDist = JointPickPx / _camera.Zoom;
        Bone? best = null;
        foreach (var b in _state.Skeleton.AllBones())
        {
            float d = Vector2.Distance(world, b.World.Translation);
            if (d <= bestDist) { best = b; bestDist = d; }
        }
        return best;
    }

    private Bone? HitBone(Vector2 world)
    {
        float bestDist = BonePickPx / _camera.Zoom;
        Bone? best = null;
        foreach (var b in _state.Skeleton.AllBones())
        {
            float d = DistanceToSegment(world, b.World.Translation, b.TipWorld);
            if (d <= bestDist) { best = b; bestDist = d; }
        }
        return best;
    }

    private static float AngleFrom(Bone bone, Vector2 world)
    {
        var v = world - bone.World.Translation;
        return MathF.Atan2(v.Y, v.X);
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}