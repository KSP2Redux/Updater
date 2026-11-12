using Ksp2Redux.Tools.Launcher.Controls;

namespace Ksp2Redux.Tools.Launcher.Tests.Controls;

public class LaunchSequenceTest
{
    private const double START = 100;

    private static LaunchSequence Launched()
    {
        var sequence = new LaunchSequence();
        sequence.Launch();
        sequence.Advance(START);
        return sequence;
    }

    [Test]
    public void Idle_StaysAtRestWithoutAnimating()
    {
        var step = new LaunchSequence().Advance(START);

        Assert.That(step, Is.EqualTo(new LaunchStep(0, LaunchFrame.Rest, false)));
    }

    [Test]
    public void DuringTheDeparture_KeepsAnimating()
    {
        var sequence = Launched();

        var step = sequence.Advance(START + LaunchTimeline.IGNITION + LaunchTimeline.BURN / 2);

        Assert.Multiple(() =>
        {
            Assert.That(step.Animating, Is.True);
            Assert.That(step.Frame.Thrust, Is.EqualTo(1));
            Assert.That(step.Frame.Travel, Is.GreaterThan(0));
        });
    }

    [Test]
    public void GameStillRunning_StationStaysHiddenAndTheFrameLoopCanStop()
    {
        var sequence = Launched();

        var justGone = sequence.Advance(START + LaunchTimeline.DEPARTURE);
        var anHourLater = sequence.Advance(START + LaunchTimeline.DEPARTURE + 3600);

        Assert.Multiple(() =>
        {
            Assert.That(justGone.Frame, Is.EqualTo(LaunchFrame.Hidden));
            Assert.That(justGone.Animating, Is.False);
            Assert.That(anHourLater.Frame, Is.EqualTo(LaunchFrame.Hidden));
            Assert.That(anHourLater.Animating, Is.False);
            Assert.That(sequence.IsActive, Is.True);
        });
    }

    [Test]
    public void GameExits_StationFadesBackAndTheSequenceEnds()
    {
        var sequence = Launched();
        var exitAt = START + LaunchTimeline.DEPARTURE + 600;
        sequence.Advance(exitAt - 1);

        Assert.That(sequence.GameExited(), Is.True);
        var returning = sequence.Advance(exitAt);
        var halfway = sequence.Advance(exitAt + LaunchTimeline.RETURN / 2);
        var done = sequence.Advance(exitAt + LaunchTimeline.RETURN + 0.01);

        Assert.Multiple(() =>
        {
            Assert.That(returning.Animating, Is.True);
            Assert.That(returning.Frame.StationOpacity, Is.Zero);
            Assert.That(halfway.Frame.StationOpacity, Is.InRange(0.3, 0.7));
            Assert.That(halfway.Frame.Thrust, Is.Zero);
            Assert.That(done, Is.EqualTo(new LaunchStep(0, LaunchFrame.Rest, false)));
            Assert.That(sequence.IsActive, Is.False);
        });
    }

    [Test]
    public void GameExitsDuringTheBurn_StationFinishesLeavingBeforeComingBack()
    {
        var sequence = Launched();
        sequence.GameExited();

        var stillBurning = sequence.Advance(START + LaunchTimeline.IGNITION + LaunchTimeline.BURN / 2);
        var gone = sequence.Advance(START + LaunchTimeline.DEPARTURE + LaunchTimeline.MINIMUM_GONE / 2);
        var returnStartsAt = START + LaunchTimeline.DEPARTURE + LaunchTimeline.MINIMUM_GONE;
        sequence.Advance(returnStartsAt);
        var returning = sequence.Advance(returnStartsAt + LaunchTimeline.RETURN / 2);

        Assert.Multiple(() =>
        {
            Assert.That(stillBurning.Frame.Thrust, Is.EqualTo(1));
            Assert.That(gone.Frame, Is.EqualTo(LaunchFrame.Hidden));
            Assert.That(gone.Animating, Is.True, "The loop has to keep running to start the return on time.");
            Assert.That(returning.Frame.StationOpacity, Is.InRange(0.3, 0.7));
        });
    }

    [Test]
    public void SecondLaunchWhileActive_IsIgnored()
    {
        var sequence = Launched();
        sequence.Advance(START + LaunchTimeline.DEPARTURE);

        Assert.That(sequence.Launch(), Is.False);
        Assert.That(sequence.Advance(START + LaunchTimeline.DEPARTURE + 1).Frame, Is.EqualTo(LaunchFrame.Hidden));
    }

    [Test]
    public void GameExitedWhileIdle_IsIgnored()
    {
        var sequence = new LaunchSequence();

        Assert.That(sequence.GameExited(), Is.False);
        Assert.That(sequence.Advance(START), Is.EqualTo(new LaunchStep(0, LaunchFrame.Rest, false)));
    }
}
