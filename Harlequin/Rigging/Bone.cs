using System.Numerics;

namespace Animator.Core.Rigging;

public class Bone
{
    public string Name { get; set; } = "";
    public Bone? Parent { get; private set; }
    public List<Bone> Children { get; } = new();

    public float Length { get; set; } = 50;

    // Local transform (relative to parent)
    public Vector2 Position { get; set; }
    public float Rotation { get; set; }          // radians
    public Vector2 Scale { get; set; } = Vector2.One;

    // Computed result
    public Matrix3x2 World { get; private set; } = Matrix3x2.Identity;
	// "Depth" rotation in radians: turning the bone in 3D, faked with scale.
	// Turn: around the bone's own length; parts narrow, then mirror ACROSS the bone at 180°.
	// Tilt: across the bone; it foreshortens, then mirrors ALONG itself at 180°.
	public float SetupTurn { get; set; }
	public float SetupTilt { get; set; }
	public float Turn { get; set; }
	public float Tilt { get; set; }

	// Scale including the depth rotation
	public Vector2 EffectiveScale => new(Scale.X * MathF.Cos(Tilt), Scale.Y * MathF.Cos(Turn));

	public void AddChild(Bone child)
    {
        child.Parent = this;
        Children.Add(child);
    }
	public void RemoveChild(Bone child)
	{
		if (Children.Remove(child)) child.Parent = null;
	}

	public bool IsDescendantOf(Bone other)
	{
		for (var b = Parent; b != null; b = b.Parent)
			if (b == other) return true;
		return false;
	}

	// Used by undo to put the hierarchy back exactly as it was.
	// "internal" = usable anywhere inside Animator.Core, invisible outside it.
	internal void RestoreLinks(Bone? parent, IEnumerable<Bone> children)
	{
		Parent = parent;
		Children.Clear();
		Children.AddRange(children);
	}
	public bool InheritRotation { get; set; } = true;
    public bool InheritScale { get; set; } = true;

    // Rest pose. Animation offsets are applied relative to these.
    public Vector2 SetupPosition { get; set; }
    public float SetupRotation { get; set; }
    public Vector2 SetupScale { get; set; } = Vector2.One;

    // Optional limits on how far the bone may move from its setup position.
    // null = unlimited on that bound.
    public Vector2? MinOffset { get; set; }
    public Vector2? MaxOffset { get; set; }

    // The bone's angle in world space
    public float WorldRotation => MathF.Atan2(World.M12, World.M11);

    // Converts a world-space point into this bone's Position space
    // (the parent's local space). Used when dragging a bone around.
    public Vector2 WorldToParentLocal(Vector2 world)
    {
        if (Parent == null) return world;
        if (!Matrix3x2.Invert(Parent.World, out var inv)) return Position; // parent scaled to zero
        return Vector2.Transform(world, inv);
    }

    public void ApplyLimits()
    {
        var offset = Position - SetupPosition;
        if (MinOffset is { } min) offset = Vector2.Max(offset, min);
        if (MaxOffset is { } max) offset = Vector2.Min(offset, max);
        Position = SetupPosition + offset;
    }

	public void ResetToSetup()
	{
		Position = SetupPosition;
		Rotation = SetupRotation;
		Scale = SetupScale;
		Turn = SetupTurn;
		Tilt = SetupTilt;
	}

	public void UpdateWorld()
	{
		var scale = EffectiveScale;   // includes the depth rotation

		if (Parent == null)
		{
			World = Matrix3x2.CreateScale(scale)
				  * Matrix3x2.CreateRotation(Rotation)
				  * Matrix3x2.CreateTranslation(Position);
		}
		else if (InheritRotation && InheritScale)
		{
			var local = Matrix3x2.CreateScale(scale)
					  * Matrix3x2.CreateRotation(Rotation)
					  * Matrix3x2.CreateTranslation(Position);
			World = local * Parent.World;
		}
		else
		{
			var p = Parent.World;
			var worldPos = Vector2.Transform(Position, p);

			var parentLinear = Matrix3x2.Identity;
			if (InheritScale)
			{
				float sx = new Vector2(p.M11, p.M12).Length();
				float sy = new Vector2(p.M21, p.M22).Length();
				parentLinear = Matrix3x2.CreateScale(sx, sy);
			}
			if (InheritRotation)
				parentLinear *= Matrix3x2.CreateRotation(MathF.Atan2(p.M12, p.M11));

			World = Matrix3x2.CreateScale(scale)
				  * Matrix3x2.CreateRotation(Rotation)
				  * parentLinear
				  * Matrix3x2.CreateTranslation(worldPos);
		}

		foreach (var child in Children)
			child.UpdateWorld();
	}

	public Vector2 TipWorld => Vector2.Transform(new Vector2(Length, 0), World);
}