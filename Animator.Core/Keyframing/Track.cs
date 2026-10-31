namespace Animator.Core.Keyframing;

public class Track<T>
{
    public List<Keyframe<T>> Keys { get; } = new();
    private readonly Func<T, T, float, T> _lerp;

    // The track needs to know how to blend two values of type T.
    public Track(Func<T, T, float, T> lerp) => _lerp = lerp;

    // curve == null means "keep the existing key's curve if replacing, else Linear".
    // That way re-keying a value never silently changes its easing.
    // bezier: the handles, for a Bezier curve
    public void Add(float time, T value, CurveType? curve = null, Bezier? bezier = null)
    {
        const float eps = 1e-4f;
        int i = Keys.FindIndex(k => k.Time >= time - eps);

        if (i >= 0 && MathF.Abs(Keys[i].Time - time) < eps)
            Keys[i] = curve == null
                ? Keys[i] with { Value = value }                                                   // replace, same easing
                : new Keyframe<T>(Keys[i].Time, value, curve.Value, bezier ?? Keys[i].Bezier);
        else
        {
            var key = new Keyframe<T>(time, value, curve ?? CurveType.Linear, bezier ?? default);
            if (i < 0) Keys.Add(key); else Keys.Insert(i, key);                       // insert sorted
        }
    }

    // A whole key (easing and all), at this time
    public void Put(float time, Keyframe<T> key) => Add(time, key.Value, key.Curve, key.Bezier);

    // Returns false if the track has no keys (so the caller leaves the bone alone).
    public bool TrySample(float time, out T value)
    {
        value = default!;
        if (Keys.Count == 0) return false;

        if (time <= Keys[0].Time) { value = Keys[0].Value; return true; }
        var last = Keys[^1];
        if (time >= last.Time) { value = last.Value; return true; }

        // Binary search: find neighbours lo, hi with Keys[lo].Time <= time < Keys[hi].Time
        int lo = 0, hi = Keys.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (Keys[mid].Time <= time) lo = mid; else hi = mid;
        }

        var a = Keys[lo];
        var b = Keys[hi];
        float t = (time - a.Time) / (b.Time - a.Time);   // 0..1 between the two keys

        value = a.Curve switch
        {
            CurveType.Stepped => a.Value,
            CurveType.Smooth => _lerp(a.Value, b.Value, t * t * (3 - 2 * t)),
            CurveType.Bezier => _lerp(a.Value, b.Value, a.Bezier.Ease(t)),
            _ => _lerp(a.Value, b.Value, t),
        };
        return true;
    }
}