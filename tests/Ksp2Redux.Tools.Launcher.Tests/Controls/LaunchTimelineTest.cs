using Ksp2Redux.Tools.Launcher.Controls;

namespace Ksp2Redux.Tools.Launcher.Tests.Controls;

public class LaunchTimelineTest
{
    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(LaunchTimeline.DURATION)]
    [TestCase(LaunchTimeline.DURATION + 5)]
    public void OutsideTheLaunch_StationIsAtRestWithNoFlame(double t)
    {
        Assert.That(LaunchTimeline.Evaluate(t), Is.EqualTo(new LaunchFrame(0, 0, 0, 0, 1)));
    }

    [Test]
    public void Ignition_LightsTheEngineBeforeTheStationMoves()
    {
        var frame = LaunchTimeline.Evaluate(LaunchTimeline.IGNITION * 0.9);

        Assert.Multiple(() =>
        {
            Assert.That(frame.Travel, Is.Zero);
            Assert.That(frame.Thrust, Is.GreaterThan(0.9));
            Assert.That(frame.StationOpacity, Is.EqualTo(1));
        });
    }

    [Test]
    public void Burn_AcceleratesTheStationAwayAtFullThrust()
    {
        var times = Enumerable.Range(1, 9).Select(i => LaunchTimeline.IGNITION + LaunchTimeline.BURN * i / 10).ToList();
        var frames = times.Select(LaunchTimeline.Evaluate).ToList();
        var steps = frames.Zip(frames.Skip(1), (a, b) => b.Travel - a.Travel).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(frames.All(f => f.Thrust == 1), Is.True);
            Assert.That(steps, Is.Ordered, "Each step should cover more ground than the last.");
            Assert.That(frames[^1].Travel, Is.GreaterThan(LaunchTimeline.TRAVEL * 0.7));
        });
    }

    [Test]
    public void AfterTheBurn_StationIsHiddenThenFadesBackInPlace()
    {
        var gone = LaunchTimeline.Evaluate(LaunchTimeline.IGNITION + LaunchTimeline.BURN + LaunchTimeline.GONE / 2);
        var returning = LaunchTimeline.Evaluate(LaunchTimeline.DURATION - LaunchTimeline.RETURN / 2);

        Assert.Multiple(() =>
        {
            Assert.That(gone.StationOpacity, Is.Zero);
            Assert.That(returning.Travel, Is.Zero);
            Assert.That(returning.Thrust, Is.Zero);
            Assert.That(returning.StationOpacity, Is.InRange(0.3, 0.7));
        });
    }
}
