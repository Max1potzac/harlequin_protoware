using System.Numerics;
using Animator.Core.Utilities;

namespace Animator.Core.Rigging;

public class IkConstraint
{
    public string Name { get; set; } = "";

    // Ordered root → tip. Each bone must be a child of the previous one.
    public List<Bone> Bones { get; } = new();

    // The bone the chain reaches for. Must NOT be a descendant of the chain.
    public Bone Target { get; set; } = null!;

    public float SetupMix { get; set; } = 1f;   // rest value (reset every frame)
    public float Mix { get; set; } = 1f;        // current value (animated)

    // +1 or -1: which way the middle joint bends (elbow/knee direction)
    public int BendDirection { get; set; } = 1;

    public int Iterations { get; set; } = 15;   // chain solver only

    public void Solve()
    {
        if (Mix <= 0f || Bones.Count == 0) return;   // pure FK: nothing to do

        var target = Target.World.Translation;

        // Remember the FK result so we can blend back toward it
        var fk = new float[Bones.Count];
        for (int i = 0; i < Bones.Count; i++) fk[i] = Bones[i].Rotation;

        if (Bones.Count == 2) SolveTwoBone(target);
        else SolveChain(target);

        for (int i = 0; i < Bones.Count; i++)
        {
            float ik = Bones[i].Rotation;
            Bones[i].Rotation = fk[i] + MathUtil.ShortestAngle(ik - fk[i]) * Mix;
        }

        Bones[0].UpdateWorld();   // refresh the chain (and its children)
    }

    private static float WorldAngle(Matrix3x2 m) => MathF.Atan2(m.M12, m.M11);

    // Angle of the bone's parent, as seen by this bone
    private static float ParentAngle(Bone b) =>
        b.Parent != null && b.InheritRotation ? WorldAngle(b.Parent.World) : 0f;

    // Exact solution using the law of cosines on the triangle
    // (root → elbow → target).
    private void SolveTwoBone(Vector2 target)
    {
        var b1 = Bones[0];
        var b2 = Bones[1];

        var root = b1.World.Translation;
        var joint = b2.World.Translation;

        float l1 = Vector2.Distance(root, joint);            // upper bone length
        float l2 = Vector2.Distance(joint, b2.TipWorld);     // lower bone length
        if (l1 < 0.01f || l2 < 0.01f) return;

        var toTarget = target - root;
        float dist = toTarget.Length();

        // If the target is too far or too close, the triangle can't exist.
        // Clamp so the limb fully straightens (or folds) instead of breaking.
        float reach = Math.Clamp(dist, MathF.Abs(l1 - l2) + 0.001f, l1 + l2 - 0.001f);

        float cosA = (l1 * l1 + reach * reach - l2 * l2) / (2 * l1 * reach);
        float a1 = MathF.Acos(Math.Clamp(cosA, -1f, 1f));

        float aim = MathF.Atan2(toTarget.Y, toTarget.X);
        float world1 = aim + BendDirection * a1;

        var elbow = root + new Vector2(MathF.Cos(world1), MathF.Sin(world1)) * l1;
        float world2 = MathF.Atan2(target.Y - elbow.Y, target.X - elbow.X);

        // Convert world angles back into local rotations
        b1.Rotation = world1 - ParentAngle(b1);
        b2.Rotation = world2 - (b2.InheritRotation ? world1 : 0f);
    }

    // CCD (Cyclic Coordinate Descent): working from the tip back to the root,
    // rotate each bone so the chain's tip swings toward the target. Repeat.
    private void SolveChain(Vector2 target)
    {
        var tipBone = Bones[^1];

        for (int iter = 0; iter < Iterations; iter++)
        {
            for (int i = Bones.Count - 1; i >= 0; i--)
            {
                var bone = Bones[i];
                var origin = bone.World.Translation;
                var toTip = tipBone.TipWorld - origin;
                var toTarget = target - origin;
                if (toTip.LengthSquared() < 1e-6f || toTarget.LengthSquared() < 1e-6f) continue;

                float delta = MathUtil.ShortestAngle(
                    MathF.Atan2(toTarget.Y, toTarget.X) - MathF.Atan2(toTip.Y, toTip.X));

                bone.Rotation += delta;
                Bones[0].UpdateWorld();   // later bones need the updated positions
            }

            if (Vector2.Distance(tipBone.TipWorld, target) < 0.01f) return;
        }
    }
}