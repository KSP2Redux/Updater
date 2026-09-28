namespace Ksp2Redux.Tools.Launcher.Controls;

public readonly record struct LaunchFrame(double Travel, double Thrust, double Flash, double Burn, double StationOpacity);

public static class LaunchTimeline
{
    public const double IGNITION = 0.4;
    public const double BURN = 2.6;
    public const double GONE = 2.0;
    public const double RETURN = 1.3;
    public const double DURATION = IGNITION + BURN + GONE + RETURN;
    public const double TRAVEL = 3300;

    public static LaunchFrame Evaluate(double t)
    {
        if (t <= 0 || t >= DURATION) return new LaunchFrame(0, 0, 0, 0, 1);

        var flash = Math.Clamp(1 - Math.Abs(t - 0.1) / 0.2, 0, 1);
        if (t < IGNITION) return new LaunchFrame(0, Smoothstep(0, 0.3, t), flash, 0, 1);

        var burn = (t - IGNITION) / BURN;
        if (burn < 1) return new LaunchFrame(TRAVEL * Math.Pow(burn, 1.8), 1, 0, burn, 1);

        var back = (t - IGNITION - BURN - GONE) / RETURN;
        return back < 0
            ? new LaunchFrame(0, 0, 0, 0, 0)
            : new LaunchFrame(0, 0, 0, 0, Smoothstep(0, 1, back));
    }

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
