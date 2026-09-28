using Ksp2Redux.Tools.Launcher.Controls;

namespace Ksp2Redux.Tools.Launcher.Tests.Controls;

public class LaunchTimelineTest
{
    [TestCase(-1)]
    [TestCase(0)]
    public void BeforeTheLaunch_StationIsAtRest(double t)
    {
        Assert.That(LaunchTimeline.Depart(t), Is.EqualTo(LaunchFrame.Rest));
    }

    [Test]
    public void Ignition_LightsTheEngineBeforeTheStationMoves()
    {
        var frame = LaunchTimeline.Depart(LaunchTimeline.IGNITION * 0.9);

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
        var frames = Enumerable.Range(1, 9)
            .Select(i => LaunchTimeline.Depart(LaunchTimeline.IGNITION + LaunchTimeline.BURN * i / 10))
            .ToList();
        var steps = frames.Zip(frames.Skip(1), (a, b) => b.Travel - a.Travel).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(frames.All(f => f.Thrust == 1), Is.True);
            Assert.That(steps, Is.Ordered, "Each step should cover more ground than the last.");
            Assert.That(frames[^1].Travel, Is.GreaterThan(LaunchTimeline.TRAVEL * 0.7));
        });
    }

    [TestCase(LaunchTimeline.DEPARTURE)]
    [TestCase(LaunchTimeline.DEPARTURE + 3600)]
    public void AfterTheBurn_StationStaysHidden(double t)
    {
        Assert.That(LaunchTimeline.Depart(t), Is.EqualTo(LaunchFrame.Hidden));
    }

    [Test]
    public void Return_FadesTheStationBackInPlace()
    {
        var halfway = LaunchTimeline.Return(LaunchTimeline.RETURN / 2);

        Assert.Multiple(() =>
        {
            Assert.That(LaunchTimeline.Return(0).StationOpacity, Is.Zero);
            Assert.That(halfway.Travel, Is.Zero);
            Assert.That(halfway.Thrust, Is.Zero);
            Assert.That(halfway.StationOpacity, Is.InRange(0.3, 0.7));
            Assert.That(LaunchTimeline.Return(LaunchTimeline.RETURN), Is.EqualTo(LaunchFrame.Rest));
        });
    }
}
