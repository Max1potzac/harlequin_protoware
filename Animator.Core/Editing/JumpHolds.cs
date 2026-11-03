using System.Numerics;

namespace Animator.Core.Editing;

// Frame-by-frame animations (games' characters converted from Animate) sometimes write a part one
// way on a frame and quite another the next, though it shows almost the same: turned a half turn
// and mirrored back, or moved to another layer's copy of itself. Each frame shows right, but
// between them, as it plays, the part swings round or flies off and back. This finds those
// frames (a part's middle half way between two frames far off the line between where it is on
// them) and holds the keys there until the next frame instead (stepped), so it plays as Animate
// shows it, frame by frame.
public static class JumpHolds
{
	public static int Hold(Skeleton skeleton, Animation animation, int fps)
	{
		int frames = (int)MathF.Round(animation.Duration * fps);
		if (frames < 1) return 0;
		var keyTimes = animation.Timelines.SelectMany(t => t.KeyTimes).ToList();
		int held = 0;
		var before = Middles(skeleton, animation, 0);
		for (int f = 0; f < frames; f++)
		{
			float t0 = f / (float)fps, t1 = (f + 1) / (float)fps;
			var half = Middles(skeleton, animation, (t0 + t1) / 2);
			var after = Middles(skeleton, animation, t1);
			bool jumps = false;
			foreach (var (slot, at) in half)
			{
				if (!before.TryGetValue(slot, out var a) || !after.TryGetValue(slot, out var b)) continue;
				float off = Vector2.Distance(at, (a + b) / 2);
				if (off > 4f + 0.3f * Vector2.Distance(a, b)) { jumps = true; break; }
			}
			if (jumps && keyTimes.Any(k => MathF.Abs(k - t0) < 1e-4f))
			{
				foreach (var timeline in animation.Timelines)
					if (timeline.KeyTimes.Any(k => MathF.Abs(k - t0) < 1e-4f)) timeline.SetCurve(t0, CurveType.Stepped);
				held++;
			}
			before = after;
		}
		skeleton.SetToSetupPose();
		skeleton.UpdatePose();
		return held;
	}

	// Where each part's picture has its middle (object space), posed at a time
	private static Dictionary<Slot, Vector2> Middles(Skeleton skeleton, Animation animation, float time)
	{
		skeleton.SetToSetupPose();
		animation.Apply(skeleton, time);
		skeleton.UpdatePose();
		var middles = new Dictionary<Slot, Vector2>();
		foreach (var slot in skeleton.Slots)
		{
			if (skeleton.ResolveAttachment(slot) is not { } drawing || !skeleton.Assets.TryGetValue(drawing.AssetId, out var asset)) continue;
			var m = drawing.LocalMatrix(asset.Width, asset.Height, slot.Offset, slot.Rotation, slot.Scale) * slot.Bone.World;
			middles[slot] = Vector2.Transform(new Vector2(asset.Width / 2f, asset.Height / 2f), m);
		}
		return middles;
	}
}
