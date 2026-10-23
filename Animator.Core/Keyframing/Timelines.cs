using System.Numerics;
using Animator.Core.Rigging;

namespace Animator.Core.Keyframing;

public abstract class Timeline
{
	public string Target { get; set; }   // bone, slot, or constraint name
	protected Timeline(string target) => Target = target;

    public abstract void Apply(Skeleton skeleton, float time);

    // ----- editor support -----
    public abstract IEnumerable<float> KeyTimes { get; }
    public abstract IEnumerable<(float Time, CurveType Curve)> KeyInfos { get; }
    public abstract object? CopyKey(float time);            // read a key without removing it
    public abstract object? TakeKey(float time);            // remove a key and return it
    public abstract void PutKey(object key, float time);    // insert (replaces any key already there)
    public abstract void SetCurve(float time, CurveType curve);

    public abstract bool RemoveKey(float time);
    public abstract object CaptureKeys();             // copy of the keys, for undo
    public abstract void RestoreKeys(object keys);


    public string Label => $"{Target} ({GetType().Name.Replace("Timeline", "")})";
}

// Shared plumbing for every timeline built on a Track<T>
public abstract class TrackTimeline<T> : Timeline
{
    public Track<T> Track { get; }

    protected TrackTimeline(string target, Func<T, T, float, T> lerp) : base(target)
        => Track = new Track<T>(lerp);

    public override IEnumerable<float> KeyTimes => Track.Keys.Select(k => k.Time);

    public override bool RemoveKey(float time) =>
        Track.Keys.RemoveAll(k => MathF.Abs(k.Time - time) < 1e-4f) > 0;

    public override object CaptureKeys() => new List<Keyframe<T>>(Track.Keys);

    public override void RestoreKeys(object keys)
    {
        Track.Keys.Clear();
        Track.Keys.AddRange((List<Keyframe<T>>)keys);
    }
    private int IndexAt(float time) =>
    Track.Keys.FindIndex(k => MathF.Abs(k.Time - time) < 1e-4f);

    public override IEnumerable<(float Time, CurveType Curve)> KeyInfos =>
        Track.Keys.Select(k => (k.Time, k.Curve));

    public override object? CopyKey(float time)
    {
        int i = IndexAt(time);
        return i < 0 ? null : Track.Keys[i];
    }

    public override object? TakeKey(float time)
    {
        int i = IndexAt(time);
        if (i < 0) return null;
        var key = Track.Keys[i];
        Track.Keys.RemoveAt(i);
        return key;
    }

    public override void PutKey(object key, float time)
    {
        var k = (Keyframe<T>)key;          // unpack: only works for this timeline's type
        Track.Add(time, k.Value, k.Curve);
    }

    public override void SetCurve(float time, CurveType curve)
    {
        int i = IndexAt(time);
        if (i >= 0) Track.Keys[i] = Track.Keys[i] with { Curve = curve };
    }
}

public class RotateTimeline : TrackTimeline<float>
{
    public RotateTimeline(string bone) : base(bone, (a, b, t) => a + (b - a) * t) { }

    public override void Apply(Skeleton skeleton, float time)
    {
        var bone = skeleton.FindBone(Target);
        if (bone != null && Track.TrySample(time, out var v))
            bone.Rotation = bone.SetupRotation + v;
    }
}

public class TranslateTimeline : TrackTimeline<Vector2>
{
    public TranslateTimeline(string bone) : base(bone, Vector2.Lerp) { }

    public override void Apply(Skeleton skeleton, float time)
    {
        var bone = skeleton.FindBone(Target);
        if (bone != null && Track.TrySample(time, out var v))
            bone.Position = bone.SetupPosition + v;
    }
}

public class ScaleTimeline : TrackTimeline<Vector2>
{
    public ScaleTimeline(string bone) : base(bone, Vector2.Lerp) { }

    public override void Apply(Skeleton skeleton, float time)
    {
        var bone = skeleton.FindBone(Target);
        if (bone != null && Track.TrySample(time, out var v))
            bone.Scale = bone.SetupScale * v;
    }
}

// Sprite swaps and frame-by-frame. The "lerp" just returns the earlier value,
// so attachments always hold until the next key, whatever the curve says.
public class AttachmentTimeline : TrackTimeline<string?>
{
    public AttachmentTimeline(string slot) : base(slot, (a, b, t) => a) { }

    public void Add(float time, string? name) => Track.Add(time, name, CurveType.Stepped);

    public override void Apply(Skeleton skeleton, float time)
    {
        var slot = skeleton.FindSlot(Target);
        if (slot == null || Track.Keys.Count == 0 || time < Track.Keys[0].Time) return;
        if (Track.TrySample(time, out var name)) slot.ActiveAttachment = name;
    }
}

public class IkMixTimeline : TrackTimeline<float>
{
    public IkMixTimeline(string constraint) : base(constraint, (a, b, t) => a + (b - a) * t) { }

    public override void Apply(Skeleton skeleton, float time)
    {
        var c = skeleton.FindIkConstraint(Target);
        if (c != null && Track.TrySample(time, out var v))
            c.Mix = Math.Clamp(v, 0f, 1f);
    }
}

// Depth rotation keys: X = tilt, Y = turn (offsets from setup, radians).
// Linear keys turn smoothly like a card; Stepped keys flip instantly.
public class DepthTimeline : TrackTimeline<Vector2>
{
	public DepthTimeline(string bone) : base(bone, Vector2.Lerp) { }

	public override void Apply(Skeleton skeleton, float time)
	{
		var bone = skeleton.FindBone(Target);
		if (bone != null && Track.TrySample(time, out var v))
		{
			bone.Tilt = bone.SetupTilt + v.X;
			bone.Turn = bone.SetupTurn + v.Y;
		}
	}
}