namespace Animator.Core.Utilities;

public static class MathUtil
{
    // Wraps an angle difference into -180..+180 degrees (in radians),
    // so blending never takes the long way around.
    public static float ShortestAngle(float a)
    {
        a %= MathF.Tau;
        if (a > MathF.PI) a -= MathF.Tau;
        else if (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}