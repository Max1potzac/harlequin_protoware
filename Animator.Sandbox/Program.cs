using System.Numerics;
using Animator.Core;

var skeleton = new Skeleton();
var upper = new Bone { Name = "upper", Length = 100 };
var fore = new Bone { Name = "fore", Length = 80, SetupPosition = new(100, 0) };
var target = new Bone { Name = "target" };
upper.AddChild(fore);
skeleton.Roots.Add(upper);
skeleton.Roots.Add(target);

var ik = new IkConstraint { Name = "arm_ik", Target = target };
ik.Bones.Add(upper);
ik.Bones.Add(fore);
skeleton.IkConstraints.Add(ik);

var cases = new (string Label, float Mix, int Bend, Vector2 Target)[]
{
	("IK, bend +1",     1f,   1, new(100, 100)),
	("IK, bend -1",     1f,  -1, new(100, 100)),
	("out of reach",    1f,   1, new(300, 0)),
	("FK only (mix 0)", 0f,   1, new(100, 100)),
	("half blend",      0.5f, 1, new(100, 100)),
};

foreach (var c in cases)
{
	ik.SetupMix = c.Mix;
	ik.BendDirection = c.Bend;
	target.SetupPosition = c.Target;

	skeleton.SetToSetupPose();
	skeleton.UpdatePose();

	Console.WriteLine($"{c.Label,-16} upper={upper.Rotation * 180 / MathF.PI,6:0.0}°  " +
					  $"fore={fore.Rotation * 180 / MathF.PI,6:0.0}°  tip={fore.TipWorld}");
}