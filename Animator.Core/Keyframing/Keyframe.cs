namespace Animator.Core.Keyframing;

public enum CurveType
{
    Linear,   // constant speed to the next key
    Stepped,  // hold this value, then jump at the next key
    Smooth    // ease in and out
}

// A "record struct" is a compact, immutable bundle of values.
public readonly record struct Keyframe<T>(float Time, T Value, CurveType Curve);