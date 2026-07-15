using Avalonia;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Tests;

public class WindowPlacementServiceTest
{
    private static readonly PixelRect PrimaryScreen = new(0, 0, 2560, 1400);
    private static readonly PixelRect SecondScreen = new(2560, 0, 2560, 1400);
    private readonly WindowPlacementService _service = new();

    [Test]
    public void NullPlacement_ReturnsNull()
    {
        Assert.That(_service.Sanitize(null, [PrimaryScreen], 1000, 700), Is.Null);
    }

    [Test]
    public void OnScreenPlacement_IsReturnedUnchanged()
    {
        var saved = new WindowPlacement { X = 100, Y = 100, Width = 1280, Height = 800, IsMaximized = false };

        var result = _service.Sanitize(saved, [PrimaryScreen], 1000, 700);

        Assert.Multiple(() =>
        {
            Assert.That(result!.X, Is.EqualTo(100));
            Assert.That(result.Y, Is.EqualTo(100));
            Assert.That(result.Width, Is.EqualTo(1280));
            Assert.That(result.Height, Is.EqualTo(800));
        });
    }

    [Test]
    public void PlacementOnAMonitorThatNoLongerExists_IsNudgedOntoTheNearestScreen()
    {
        // Saved while a second monitor was attached; only the primary remains.
        var saved = new WindowPlacement { X = 3000, Y = 200, Width = 1280, Height = 800 };

        var result = _service.Sanitize(saved, [PrimaryScreen], 1000, 700);

        Assert.Multiple(() =>
        {
            Assert.That(result!.X, Is.LessThanOrEqualTo(PrimaryScreen.Right - 1280));
            Assert.That(result.X, Is.GreaterThanOrEqualTo(PrimaryScreen.X));
            Assert.That(result.Y, Is.EqualTo(200), "Y was already fine and should not move.");
            Assert.That(result.Width, Is.EqualTo(1280), "The user's chosen size must survive the nudge.");
        });
    }

    [Test]
    public void PlacementStillOnTheSecondMonitor_IsLeftAloneWhenThatMonitorExists()
    {
        var saved = new WindowPlacement { X = 3000, Y = 200, Width = 1280, Height = 800 };

        var result = _service.Sanitize(saved, [PrimaryScreen, SecondScreen], 1000, 700);

        Assert.That(result!.X, Is.EqualTo(3000));
    }

    [Test]
    public void SizeBelowTheMinimum_IsRaisedToTheMinimum()
    {
        var saved = new WindowPlacement { X = 100, Y = 100, Width = 640, Height = 480 };

        var result = _service.Sanitize(saved, [PrimaryScreen], 1000, 700);

        Assert.Multiple(() =>
        {
            Assert.That(result!.Width, Is.EqualTo(1000));
            Assert.That(result.Height, Is.EqualTo(700));
        });
    }

    [TestCase(double.NaN, 800)]
    [TestCase(1280, double.PositiveInfinity)]
    [TestCase(0, 800)]
    [TestCase(-500, 800)]
    public void GarbageDimensions_ReturnNullSoDefaultsApply(double width, double height)
    {
        var saved = new WindowPlacement { X = 100, Y = 100, Width = width, Height = height };

        Assert.That(_service.Sanitize(saved, [PrimaryScreen], 1000, 700), Is.Null);
    }

    [Test]
    public void NoScreenInformation_ReturnsThePlacementWithoutValidation()
    {
        var saved = new WindowPlacement { X = 100, Y = 100, Width = 1280, Height = 800 };

        var result = _service.Sanitize(saved, [], 1000, 700);

        Assert.That(result!.X, Is.EqualTo(100));
    }

    [Test]
    public void WindowAboveTheVisibleArea_IsPulledDownSoTheTitleBarIsGrabbable()
    {
        // Title bar way above the top edge of every screen - nothing to grab and drag.
        var saved = new WindowPlacement { X = 100, Y = -2000, Width = 1280, Height = 800 };

        var result = _service.Sanitize(saved, [PrimaryScreen], 1000, 700);

        Assert.That(result!.Y, Is.GreaterThanOrEqualTo(PrimaryScreen.Y));
    }

    [Test]
    public void MaximizedFlag_IsPreserved()
    {
        var saved = new WindowPlacement { X = 100, Y = 100, Width = 1280, Height = 800, IsMaximized = true };

        Assert.That(_service.Sanitize(saved, [PrimaryScreen], 1000, 700)!.IsMaximized, Is.True);
    }
}
