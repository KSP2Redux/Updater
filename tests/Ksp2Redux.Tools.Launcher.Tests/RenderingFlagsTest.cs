using Avalonia;

namespace Ksp2Redux.Tools.Launcher.Tests;

public class RenderingFlagsTest
{
    [Test]
    public void NoFlags_LeavesAvaloniaDefaults()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Program.CreateWin32Options(false, false), Is.Null);
            Assert.That(Program.GetRenderingFlags(false, false), Is.Empty);
        });
    }

    [Test]
    public void SoftwareRendering_UsesCpuAndPlainWindowSurface()
    {
        var options = Program.CreateWin32Options(true, false)!;

        Assert.Multiple(() =>
        {
            Assert.That(options.RenderingMode, Is.EqualTo(new[] { Win32RenderingMode.Software }));
            Assert.That(options.CompositionMode, Is.EqualTo(new[] { Win32CompositionMode.RedirectionSurface }));
        });
    }

    [Test]
    public void NoComposition_KeepsGpuButSkipsWinUIComposition()
    {
        var options = Program.CreateWin32Options(false, true)!;
        var defaults = new Win32PlatformOptions();

        Assert.Multiple(() =>
        {
            Assert.That(options.RenderingMode, Is.EqualTo(defaults.RenderingMode));
            Assert.That(options.CompositionMode, Does.Not.Contain(Win32CompositionMode.WinUIComposition));
            Assert.That(options.CompositionMode, Does.Contain(Win32CompositionMode.RedirectionSurface));
        });
    }

    [Test]
    public void SoftwareRendering_WinsWhenBothAreGiven()
    {
        var options = Program.CreateWin32Options(true, true)!;

        Assert.That(options.RenderingMode, Is.EqualTo(new[] { Win32RenderingMode.Software }));
    }

    [Test]
    public void Flags_AreFormattedForTheUpdateRelaunch()
    {
        Assert.That(Program.GetRenderingFlags(true, true), Is.EqualTo("--software-rendering --no-composition"));
    }
}
