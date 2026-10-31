using System.Numerics;

namespace Animator.Core.Rigging;

public enum HitboxShape { Box, Circle }

// A hitbox: a box or circle on a bone, moving with it, for the game to test hits against. Its
// kind says what it's for (hurt: where the object can be hit; hit: what hits others; anything
// else the game knows). It can be on or off, and keyed so in each animation (an attack's hitbox
// only on the frames that strike).
public sealed class Hitbox
{
	public static readonly string[] CommonKinds = { "hurt", "hit", "block", "trigger" };

	public string Name { get; set; } = "hitbox";
	public Bone Bone { get; set; }
	public HitboxShape Shape { get; set; }
	public Vector2 Offset { get; set; }                 // its centre, in the bone's space
	public float Rotation { get; set; }                 // radians (a box's; a circle doesn't care)
	public Vector2 Size { get; set; } = new(40, 40);    // a box's width and height; a circle's diameter (X)
	public string Kind { get; set; } = "hurt";
	public bool SetupActive { get; set; } = true;
	public bool Active { get; set; } = true;            // now (keys change it; back to SetupActive every frame)

	public Hitbox(string name, Bone bone) { Name = name; Bone = bone; }

	public float Radius => Size.X / 2f;

	// Its own space (centre at 0, axes along its sides) → the object's
	public Matrix3x2 World => Matrix3x2.CreateRotation(Rotation) * Matrix3x2.CreateTranslation(Offset) * Bone.World;

	// Its outline in the object's space: a box's four corners, or a circle as many points
	public Vector2[] Outline(int circleSteps = 32)
	{
		var m = World;
		if (Shape == HitboxShape.Box)
		{
			float w = Size.X / 2f, h = Size.Y / 2f;
			return new[] { new Vector2(-w, -h), new Vector2(w, -h), new Vector2(w, h), new Vector2(-w, h) }
				.Select(p => Vector2.Transform(p, m)).ToArray();
		}
		return Enumerable.Range(0, circleSteps)
			.Select(i => Vector2.Transform(new Vector2(MathF.Cos(i * MathF.Tau / circleSteps), MathF.Sin(i * MathF.Tau / circleSteps)) * Radius, m))
			.ToArray();
	}

	// Whether a point of the object's space is inside it
	public bool Contains(Vector2 point)
	{
		if (!Matrix3x2.Invert(World, out var toLocal)) return false;
		var p = Vector2.Transform(point, toLocal);
		return Shape == HitboxShape.Box
			? MathF.Abs(p.X) <= Size.X / 2f && MathF.Abs(p.Y) <= Size.Y / 2f
			: p.Length() <= Radius;
	}
}

// A point on a bone, moving with it: where the game spawns something or attaches something (a
// gun's muzzle, a mouth, a hand to hold an item), and which way it faces.
public sealed class RigPoint
{
	public string Name { get; set; } = "point";
	public Bone Bone { get; set; }
	public Vector2 Offset { get; set; }   // in the bone's space
	public float Rotation { get; set; }   // radians, from the bone's direction

	public RigPoint(string name, Bone bone) { Name = name; Bone = bone; }

	public Matrix3x2 World => Matrix3x2.CreateRotation(Rotation) * Matrix3x2.CreateTranslation(Offset) * Bone.World;
	public Vector2 WorldPosition => World.Translation;
	public float WorldAngle { get { var m = World; return MathF.Atan2(m.M12, m.M11); } }
}
