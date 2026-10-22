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

    public void AddChild(Bone child)
    {
        child.Parent = this;
        Children.Add(child);
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
    }

    public void UpdateWorld()
    {
        if (Parent == null)
        {
            World = Matrix3x2.CreateScale(Scale)
                  * Matrix3x2.CreateRotation(Rotation)
                  * Matrix3x2.CreateTranslation(Position);
        }
        else if (InheritRotation && InheritScale)
        {
            // Normal case: everything is inherited
            var local = Matrix3x2.CreateScale(Scale)
                      * Matrix3x2.CreateRotation(Rotation)
                      * Matrix3x2.CreateTranslation(Position);
            World = local * Parent.World;
        }
        else
        {
            var p = Parent.World;

            // Position always follows the parent, so the bone still orbits with it
            var worldPos = Vector2.Transform(Position, p);

            // Rebuild only the parts of the parent we want to inherit
            var parentLinear = Matrix3x2.Identity;
            if (InheritScale)
            {
                float sx = new Vector2(p.M11, p.M12).Length();
                float sy = new Vector2(p.M21, p.M22).Length();
                parentLinear = Matrix3x2.CreateScale(sx, sy);
            }
            if (InheritRotation)
                parentLinear *= Matrix3x2.CreateRotation(MathF.Atan2(p.M12, p.M11));

            World = Matrix3x2.CreateScale(Scale)
                  * Matrix3x2.CreateRotation(Rotation)
                  * parentLinear
                  * Matrix3x2.CreateTranslation(worldPos);
        }

        foreach (var child in Children)
            child.UpdateWorld();
    }

    public Vector2 TipWorld => Vector2.Transform(new Vector2(Length, 0), World);
}