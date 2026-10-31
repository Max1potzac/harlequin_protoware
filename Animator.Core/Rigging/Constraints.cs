using System.Numerics;

namespace Animator.Core.Rigging;

// A bone made to follow another one's turn, place or size, partly or fully (Spine's transform
// constraint): shoulder pads riding an arm, a gear turning with another, a prop copying a hand.
// - World: it takes the source's world angle, place and size, plus the offsets it had when
//   made (so it starts where it was), each by its mix.
// - Local: it takes how the source moves from its own rest (turn, move, size), on top of its own.
public class CopyConstraint
{
	public string Name { get; set; } = "";
	public Bone Bone { get; set; } = null!;
	public Bone Source { get; set; } = null!;
	public bool Local { get; set; }
	public float RotateMix { get; set; } = 1f;
	public float MoveMix { get; set; }
	public float ScaleMix { get; set; }
	public float RotationOffset { get; set; }                // World: its angle less the source's
	public Vector2 PositionOffset { get; set; }              // World: where its joint is, in the source's space
	public Vector2 ScaleOffset { get; set; } = Vector2.One;  // World: its size over the source's
	public float SetupMix { get; set; } = 1f;
	public float Mix { get; set; } = 1f;

	// Its settings, for undo: a copy, and back from one
	public CopyConstraint Settings() => (CopyConstraint)MemberwiseClone();
	public void Take(CopyConstraint s) =>
		(Name, Bone, Source, Local, RotateMix, MoveMix, ScaleMix, RotationOffset, PositionOffset, ScaleOffset, SetupMix) =
		(s.Name, s.Bone, s.Source, s.Local, s.RotateMix, s.MoveMix, s.ScaleMix, s.RotationOffset, s.PositionOffset, s.ScaleOffset, s.SetupMix);

	// The offsets that keep it where it is now
	public void KeepOffsets()
	{
		RotationOffset = Bone.WorldRotation - Source.WorldRotation;
		PositionOffset = Matrix3x2.Invert(Source.World, out var toSource) ? Vector2.Transform(Bone.World.Translation, toSource) : Vector2.Zero;
		ScaleOffset = new Vector2(Ratio(Bone.Scale.X, Source.Scale.X), Ratio(Bone.Scale.Y, Source.Scale.Y));
	}

	private static float Ratio(float a, float b) => MathF.Abs(b) > 1e-6f ? a / b : 1f;

	public void Solve()
	{
		if (Mix <= 0f || Source == Bone) return;
		float turn = Math.Clamp(RotateMix, 0f, 1f) * Mix, move = Math.Clamp(MoveMix, 0f, 1f) * Mix, size = Math.Clamp(ScaleMix, 0f, 1f) * Mix;
		if (Local)
		{
			Bone.Rotation += (Source.Rotation - Source.SetupRotation) * turn;
			Bone.Position += (Source.Position - Source.SetupPosition) * move;
			var ratio = new Vector2(Ratio(Source.Scale.X, Source.SetupScale.X), Ratio(Source.Scale.Y, Source.SetupScale.Y));
			Bone.Scale *= Vector2.Lerp(Vector2.One, ratio, size);
		}
		else
		{
			if (move > 0f)
			{
				var at = Bone.WorldToParentLocal(Vector2.Transform(PositionOffset, Source.World));
				Bone.Position = Vector2.Lerp(Bone.Position, at, move);
				Bone.UpdateWorld();
			}
			if (turn > 0f)
				Bone.Rotation += MathUtil.ShortestAngle(Source.WorldRotation + RotationOffset - Bone.WorldRotation) * turn;
			if (size > 0f)
				Bone.Scale = Vector2.Lerp(Bone.Scale, Source.Scale * ScaleOffset, size);
		}
		Bone.UpdateWorld();
	}
}

// Secondary motion (Spine's physics constraint): a bone that lags behind as it's moved and
// springs back, swinging past and settling (hair, a tail, ears, an antenna). Its tip is a weight
// on a spring pulled toward where the animation puts it; the bone turns to point at the weight.
// It needs time going by: playing (and exports) step it frame by frame; jumping back, or far
// ahead, starts it again from rest.
public class JiggleConstraint
{
	public string Name { get; set; } = "";
	public Bone Bone { get; set; } = null!;
	public float Stiffness { get; set; } = 80f;   // how hard the spring pulls (higher: quicker, less swing)
	public float Damping { get; set; } = 6f;      // how fast the swinging dies down
	public float Gravity { get; set; }            // pulls the weight down (object units per second²)
	public float SetupMix { get; set; } = 1f;
	public float Mix { get; set; } = 1f;

	private bool _moving;
	private float _time;
	private Vector2 _tip, _velocity;

	public void Reset() => _moving = false;

	public JiggleConstraint Settings() => (JiggleConstraint)MemberwiseClone();
	public void Take(JiggleConstraint s) =>
		(Name, Bone, Stiffness, Damping, Gravity, SetupMix) = (s.Name, s.Bone, s.Stiffness, s.Damping, s.Gravity, s.SetupMix);

	// One step to this time (the animation already applied, the bones' world places worked out)
	public void Step(float time)
	{
		var head = Bone.World.Translation;
		var wanted = Bone.TipWorld;
		float length = Vector2.Distance(head, wanted);
		if (length < 1e-4f) return;
		if (!_moving || time < _time - 1e-5f || time - _time > 1f)
		{
			_moving = true;
			_time = time;
			_tip = wanted;
			_velocity = Vector2.Zero;
			return;
		}

		float dt = time - _time;
		_time = time;
		if (dt > 0f)
		{
			int steps = Math.Max(1, (int)MathF.Ceiling(dt * 240f));
			float h = dt / steps;
			for (int i = 0; i < steps; i++)
			{
				var pull = (wanted - _tip) * Stiffness - _velocity * Damping + new Vector2(0f, -Gravity);
				_velocity += pull * h;
				_tip += _velocity * h;
				var along = _tip - head;   // the bone keeps its length
				if (along.LengthSquared() <= 1e-8f) continue;
				var direction = Vector2.Normalize(along);
				_tip = head + direction * length;
				_velocity -= direction * Vector2.Dot(_velocity, direction);   // (only swinging round, not along it)
			}
		}

		if (Mix <= 0f) return;
		float angle = MathF.Atan2((_tip - head).Y, (_tip - head).X) - MathF.Atan2((wanted - head).Y, (wanted - head).X);
		Bone.Rotation += MathUtil.ShortestAngle(angle) * Math.Clamp(Mix, 0f, 1f);
		Bone.UpdateWorld();
	}
}
