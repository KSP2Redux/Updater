using Avalonia.Logging;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Infrastructure;

[NonParallelizable]
public class AvaloniaLogSinkTest
{
    private Mock<ILogService> _log = null!;

    [SetUp]
    public void SetUp()
    {
        _log = new Mock<ILogService>();
        AvaloniaLogSink.Target = _log.Object;
    }

    [TearDown]
    public void TearDown()
    {
        AvaloniaLogSink.Target = null;
    }

    [TestCase(LogEventLevel.Warning, LogArea.Win32Platform, true)]
    [TestCase(LogEventLevel.Warning, LogArea.Visual, true)]
    [TestCase(LogEventLevel.Warning, LogArea.Binding, false)]
    [TestCase(LogEventLevel.Warning, LogArea.Property, false)]
    [TestCase(LogEventLevel.Error, LogArea.Binding, true)]
    [TestCase(LogEventLevel.Information, LogArea.Win32Platform, false)]
    public void IsEnabled_KeepsPlatformWarningsAndAllErrors(LogEventLevel level, string area, bool expected)
    {
        Assert.That(new AvaloniaLogSink().IsEnabled(level, area), Is.EqualTo(expected));
    }

    [Test]
    public void Log_WarningGoesToWarnWithValuesFilledIn()
    {
        new AvaloniaLogSink().Log(LogEventLevel.Warning, LogArea.Win32Platform, null,
            "Could not use {Mode}: {Reason}", "AngleEgl", "no device");

        _log.Verify(l => l.Warn("[Avalonia Win32Platform] Could not use AngleEgl: no device", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void Log_ErrorGoesToError()
    {
        new AvaloniaLogSink().Log(LogEventLevel.Fatal, LogArea.Platform, null, "Broke");

        _log.Verify(l => l.Error("[Avalonia Platform] Broke", null, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void Log_DisabledLevelIsDropped()
    {
        new AvaloniaLogSink().Log(LogEventLevel.Warning, LogArea.Binding, null, "Noisy {Path}", "Foo");

        _log.VerifyNoOtherCalls();
    }

    [Test]
    public void Format_LeavesPlaceholdersWithoutValues()
    {
        Assert.That(AvaloniaLogSink.Format("{A} and {B}", ["x"]), Is.EqualTo("x and {B}"));
    }
}
