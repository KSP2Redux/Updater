namespace Ksp2Redux.Tools.Launcher.Controls;

public readonly record struct LaunchStep(double Time, LaunchFrame Frame, bool Animating);

public sealed class LaunchSequence
{
    private double? _launchedAt;
    private double? _returningAt;
    private bool _launchPending;
    private bool _exitPending;

    public bool IsActive => _launchedAt is not null || _launchPending;

    public bool Launch()
    {
        if (IsActive) return false;
        _launchPending = true;
        return true;
    }

    public bool GameExited()
    {
        if (!IsActive) return false;
        _exitPending = true;
        return true;
    }

    public LaunchStep Advance(double now)
    {
        if (_launchPending)
        {
            _launchPending = false;
            _launchedAt = now;
            _returningAt = null;
        }
        if (_launchedAt is not { } launchedAt) return new LaunchStep(0, LaunchFrame.Rest, false);

        var sinceLaunch = now - launchedAt;
        if (_returningAt is null && _exitPending && sinceLaunch >= LaunchTimeline.DEPARTURE + LaunchTimeline.MINIMUM_GONE)
        {
            _exitPending = false;
            _returningAt = now;
        }

        if (_returningAt is { } returningAt)
        {
            var sinceReturn = now - returningAt;
            if (sinceReturn < LaunchTimeline.RETURN) return new LaunchStep(sinceLaunch, LaunchTimeline.Return(sinceReturn), true);
            _launchedAt = null;
            _returningAt = null;
            return new LaunchStep(0, LaunchFrame.Rest, false);
        }

        return new LaunchStep(sinceLaunch, LaunchTimeline.Depart(sinceLaunch), sinceLaunch < LaunchTimeline.DEPARTURE || _exitPending);
    }
}
