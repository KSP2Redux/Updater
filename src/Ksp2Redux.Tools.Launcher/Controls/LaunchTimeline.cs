namespace Ksp2Redux.Tools.Launcher.Controls;

public readonly record struct LaunchFrame(double Travel, double Thrust, double Flash, double Burn, double StationOpacity)
{
    public static LaunchFrame Rest { get; } = new(0, 0, 0, 0, 1);
    public static LaunchFrame Hidden { get; } = new(0, 0, 0, 0, 0);
}

public static class LaunchTimeline
{
    public const double IGNITION = 0.4;
    public const double BURN = 2.6;
    public const double DEPARTURE = IGNITION + BURN;
    public const double MINIMUM_GONE = 1.0;
    public const double RETURN = 1.3;
    public const double TRAVEL = 3300;

    public static LaunchFrame Depart(double t)
    {
        if (t <= 0) return LaunchFrame.Rest;

        var flash = Math.Clamp(1 - Math.Abs(t - 0.1) / 0.2, 0, 1);
        if (t < IGNITION) return new LaunchFrame(0, Smoothstep(0, 0.3, t), flash, 0, 1);

        var burn = (t - IGNITION) / BURN;
        return burn < 1
            ? new LaunchFrame(TRAVEL * Math.Pow(burn, 1.8), 1, 0, burn, 1)
            : LaunchFrame.Hidden;
    }

    public static LaunchFrame Return(double t) =>
        t >= RETURN ? LaunchFrame.Rest : new LaunchFrame(0, 0, 0, 0, Smoothstep(0, 1, t / RETURN));

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
