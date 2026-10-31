using System.Numerics;
using Animator.Core.Keyframing;
using Animator.Core.Rigging;

namespace Animator.Core.Editing;

// Mixing animations: one laid over another by a weight (tracks: walking legs under shooting
// arms), and one crossfading into the next (walk to run). An animation only changes what it has
// keys for, so a track above changes just its own bones and parts and leaves the rest to the
// tracks below. Values blend (shortest way round, for angles); what can't (which drawing shows,
// the draw order, a hitbox on or off) switches halfway.
public static class Mixing
{
	// The animation at this time over what's posed now, by alpha (0: not at all, 1: fully)
	public static void Layer(Skeleton skeleton, Animation animation, float time, float alpha)
	{
		alpha = Math.Clamp(alpha, 0f, 1f);
		if (alpha <= 0f) return;
		if (alpha >= 1f)
		{
			animation.Apply(skeleton, time);
			return;
		}
		var before = Pose.Of(skeleton);
		animation.Apply(skeleton, time);
		Pose.Of(skeleton).BlendFrom(skeleton, before, alpha);
	}

	// Crossfading: `from` fully, then `to` over it by how far the fade has gone (0..1)
	public static void Crossfade(Skeleton skeleton, Animation from, float fromTime, Animation to, float toTime, float progress)
	{
		Layer(skeleton, from, fromTime, 1f);
		Layer(skeleton, to, toTime, progress);
	}

	// Everything an animation can change, at one moment
	private sealed class Pose
	{
		private readonly List<(Bone Bone, Vector2 Position, float Rotation, Vector2 Scale, float Turn, float Tilt)> _bones = new();
		private readonly List<(Slot Slot, Vector2 Offset, float Rotation, Vector2 Scale, Vector3 Color, float Opacity, string? Drawing, DeformKey? Deform)> _parts = new();
		private readonly List<string> _order = new();
		private readonly List<float> _mixes = new();
		private readonly List<AdjustValues> _colours = new();
		private readonly List<bool> _hitboxes = new();

		public static Pose Of(Skeleton s)
		{
			var p = new Pose();
			foreach (var b in s.AllBones()) p._bones.Add((b, b.Position, b.Rotation, b.Scale, b.Turn, b.Tilt));
			foreach (var slot in s.Slots) p._parts.Add((slot, slot.Offset, slot.Rotation, slot.Scale, slot.Color, slot.Opacity, slot.ActiveAttachment, slot.Deform));
			p._order.AddRange(s.DrawOrder.Select(x => x.Name));
			p._mixes.AddRange(s.IkConstraints.Select(c => c.Mix).Concat(s.LookConstraints.Select(c => c.Mix))
				.Concat(s.CopyConstraints.Select(c => c.Mix)).Concat(s.JiggleConstraints.Select(c => c.Mix)));
			p._colours.AddRange(s.ColorLayers.Select(l => l.Adjust.Now));
			p._hitboxes.AddRange(s.Hitboxes.Select(h => h.Active));
			return p;
		}

		// The skeleton (now posed as `this`) put partway: from `before`, `alpha` of the way to this
		public void BlendFrom(Skeleton s, Pose before, float alpha)
		{
			bool switchNow = alpha >= 0.5f;
			for (int i = 0; i < _bones.Count; i++)
			{
				var (b, position, rotation, scale, turn, tilt) = _bones[i];
				var was = before._bones[i];
				b.Position = Vector2.Lerp(was.Position, position, alpha);
				b.Rotation = was.Rotation + MathUtil.ShortestAngle(rotation - was.Rotation) * alpha;
				b.Scale = Vector2.Lerp(was.Scale, scale, alpha);
				b.Turn = was.Turn + MathUtil.ShortestAngle(turn - was.Turn) * alpha;
				b.Tilt = was.Tilt + MathUtil.ShortestAngle(tilt - was.Tilt) * alpha;
			}
			for (int i = 0; i < _parts.Count; i++)
			{
				var now = _parts[i];
				var was = before._parts[i];
				var slot = now.Slot;
				slot.Offset = Vector2.Lerp(was.Offset, now.Offset, alpha);
				slot.Rotation = was.Rotation + MathUtil.ShortestAngle(now.Rotation - was.Rotation) * alpha;
				slot.Scale = Vector2.Lerp(was.Scale, now.Scale, alpha);
				slot.Color = Vector3.Lerp(was.Color, now.Color, alpha);
				slot.Opacity = was.Opacity + (now.Opacity - was.Opacity) * alpha;
				slot.ActiveAttachment = switchNow ? now.Drawing : was.Drawing;
				slot.Deform = BlendDeform(was.Deform, now.Deform, was.Drawing, now.Drawing, alpha, switchNow);
			}
			if (!switchNow && !_order.SequenceEqual(before._order)) s.SetDrawOrder(before._order);
			int m = 0;
			foreach (var c in s.IkConstraints) c.Mix = Lerp(before._mixes[m], _mixes[m++], alpha);
			foreach (var c in s.LookConstraints) c.Mix = Lerp(before._mixes[m], _mixes[m++], alpha);
			foreach (var c in s.CopyConstraints) c.Mix = Lerp(before._mixes[m], _mixes[m++], alpha);
			foreach (var c in s.JiggleConstraints) c.Mix = Lerp(before._mixes[m], _mixes[m++], alpha);
			for (int i = 0; i < _colours.Count; i++) s.ColorLayers[i].Adjust.Now = AdjustValues.Lerp(before._colours[i], _colours[i], alpha);
			for (int i = 0; i < _hitboxes.Count; i++) s.Hitboxes[i].Active = switchNow ? _hitboxes[i] : before._hitboxes[i];
		}

		private static float Lerp(float a, float b, float t) => a + (b - a) * t;

		// Pushes on the same drawing blend (none counts as zero); else they switch with the drawing
		private static DeformKey? BlendDeform(DeformKey? was, DeformKey? now, string? wasDrawing, string? nowDrawing, float alpha, bool switchNow)
		{
			if (wasDrawing != nowDrawing) return switchNow ? now : was;
			if (was == null && now == null) return null;
			string drawing = (now ?? was)!.Drawing;
			int n = (now ?? was)!.Offsets.Length;
			var a = was is { } w && w.Drawing == drawing && w.Offsets.Length == n ? w.Offsets : new Vector2[n];
			var b = now is { } x && x.Drawing == drawing && x.Offsets.Length == n ? x.Offsets : new Vector2[n];
			var mixed = new Vector2[n];
			for (int i = 0; i < n; i++) mixed[i] = Vector2.Lerp(a[i], b[i], alpha);
			return new DeformKey(drawing, mixed);
		}
	}
}

// How long one animation takes to fade into another (for the game; the mixer tries them out).
// Pairs not listed take the default.
public sealed class MixPlan
{
	public float Default { get; set; } = 0.2f;   // seconds
	public List<(string From, string To, float Duration)> Pairs { get; } = new();

	public float Duration(string from, string to) =>
		Pairs.FindLast(p => p.From == from && p.To == to) is { From: not null } pair ? pair.Duration : Default;

	public void Set(string from, string to, float duration)
	{
		Pairs.RemoveAll(p => p.From == from && p.To == to);
		Pairs.Add((from, to, MathF.Max(0f, duration)));
	}

	public bool Remove(string from, string to) => Pairs.RemoveAll(p => p.From == from && p.To == to) > 0;

	// An animation renamed: its pairs follow
	public void Rename(string oldName, string newName)
	{
		for (int i = 0; i < Pairs.Count; i++)
		{
			var (from, to, d) = Pairs[i];
			Pairs[i] = (from == oldName ? newName : from, to == oldName ? newName : to, d);
		}
	}

	public MixPlan Copy()
	{
		var copy = new MixPlan { Default = Default };
		copy.Pairs.AddRange(Pairs);
		return copy;
	}

	public void Take(MixPlan other)
	{
		Default = other.Default;
		Pairs.Clear();
		Pairs.AddRange(other.Pairs);
	}
}

// One track of the mixer: an animation playing on its own clock, laid over the tracks below by
// its weight
public sealed class MixTrack
{
	public Animation? Animation { get; set; }
	public float Weight { get; set; } = 1f;
	public float Speed { get; set; } = 1f;
	public bool Loop { get; set; } = true;

	public float TimeAt(float clock)
	{
		if (Animation is not { } a || a.Duration <= 0f) return 0f;
		float t = clock * Speed;
		return Loop ? ((t % a.Duration) + a.Duration) % a.Duration : Math.Clamp(t, 0f, a.Duration);
	}
}

// The mixer's preview: tracks over each other, or a transition tried out (the first animation
// plays once through, then fades into the second over the duration, which then plays on; and
// round again)
public sealed class MixPreview
{
	public List<MixTrack> Tracks { get; } = new() { new MixTrack(), new MixTrack(), new MixTrack() };
	public (Animation From, Animation To, float Duration)? Transition { get; set; }

	// How long one round of the transition takes
	public float TransitionLength => Transition is { } t ? First(t.From) + MathF.Max(t.To.Duration, t.Duration) + 0.25f : 0f;

	private static float First(Animation a) => a.Duration > 0 ? a.Duration : 1f;

	public void Pose(Skeleton skeleton, float clock)
	{
		skeleton.SetToSetupPose();
		if (Transition is { } t)
		{
			float round = TransitionLength;
			float time = round > 0 ? clock % round : 0f;
			float switchAt = First(t.From);
			if (time < switchAt) Mixing.Layer(skeleton, t.From, time, 1f);
			else
			{
				float since = time - switchAt;
				float progress = t.Duration > 0 ? Math.Clamp(since / t.Duration, 0f, 1f) : 1f;
				float fromTime = t.From.Loop && t.From.Duration > 0 ? time % t.From.Duration : MathF.Min(time, t.From.Duration);
				float toTime = t.To.Loop && t.To.Duration > 0 ? since % t.To.Duration : MathF.Min(since, t.To.Duration);
				Mixing.Crossfade(skeleton, t.From, fromTime, t.To, toTime, progress);
			}
		}
		else
			foreach (var track in Tracks)
				if (track.Animation is { } a) Mixing.Layer(skeleton, a, track.TimeAt(clock), track.Weight);
		skeleton.UpdatePose();
	}
}
