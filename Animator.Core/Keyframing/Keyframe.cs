namespace Animator.Core.Keyframing;

public enum CurveType
{
    Linear,   // constant speed to the next key
    Stepped,  // hold this value, then jump at the next key
    Smooth,   // ease in and out
    Bezier    // eased the way its Bézier handles say (the graph editor)
}

// A "record struct" is a compact, immutable bundle of values. Curve: how the stretch from this key
// to the next goes; Bezier: its handles, for a Bezier curve.
public readonly record struct Keyframe<T>(float Time, T Value, CurveType Curve, Bezier Bezier = default)
{
    // The handles that draw this key's stretch, whatever its curve (none for a stepped one)
    public Bezier? Handles => Curve switch
    {
        CurveType.Bezier => Bezier,
        CurveType.Smooth => Keyframing.Bezier.SmoothLike,
        CurveType.Linear => Keyframing.Bezier.Straight,
        _ => null
    };
}