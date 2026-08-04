using Ksp2Redux.Tools.Cli;
using Ksp2Redux.Tools.Launcher.Models;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

public class CliContextTest
{
    private static GameVersion Version(string versionNumber, string buildNumber, string channel = "internal")
        => new()
        {
            VersionNumber = System.Version.Parse(versionNumber),
            BuildNumber = buildNumber,
            Channel = channel,
        };

    private static GameVersion[] SampleVersions() =>
    [
        Version("0.2.3.0", "101669"),
        Version("0.2.9.0", "103477"),
        Version("0.2.9.0", "103669"),
    ];

    [Test]
    public void FormatVersion_VersionAndBuild_JoinedByDot()
    {
        // Arrange
        GameVersion version = Version("0.2.9.0", "103669");

        // Act
        string result = CliContext.FormatVersion(version);

        // Assert
        Assert.That(result, Is.EqualTo("0.2.9.0.103669"));
    }

    [Test]
    public void FindVersion_FullVersionString_MatchesThatVersion()
    {
        // Act
        GameVersion? result = CliContext.FindVersion(SampleVersions(), "0.2.9.0.103477");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.BuildNumber, Is.EqualTo("103477"));
    }

    // The version number prefix changes over a channel's life, so build 101669 is 0.2.3.0 while
    // 103669 is 0.2.9.0. A caller that only knows the build number has to be able to ask for it.
    [Test]
    public void FindVersion_BareBuildNumber_MatchesRegardlessOfVersionPrefix(
        [Values("101669", "103477", "103669")] string buildNumber)
    {
        // Act
        GameVersion? result = CliContext.FindVersion(SampleVersions(), buildNumber);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.BuildNumber, Is.EqualTo(buildNumber));
    }

    [Test]
    public void FindVersion_SurroundingWhitespace_StillMatches()
    {
        // Act
        GameVersion? result = CliContext.FindVersion(SampleVersions(), "  103669  ");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.BuildNumber, Is.EqualTo("103669"));
    }

    [Test]
    public void FindVersion_UnknownSelector_ReturnsNull(
        [Values("999999", "0.2.9.0.999999", "", "not-a-version")] string selector)
    {
        // Act
        GameVersion? result = CliContext.FindVersion(SampleVersions(), selector);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveChannel_ExplicitChannel_WinsOverInstall()
    {
        // Arrange
        Ksp2InstallEntry entry = new() { ReleaseChannel = "internal" };

        // Act
        string? result = CliContext.ResolveChannel("beta", entry);

        // Assert
        Assert.That(result, Is.EqualTo("beta"));
    }

    [Test]
    public void ResolveChannel_NoExplicitChannel_FallsBackToInstall()
    {
        // Arrange
        Ksp2InstallEntry entry = new() { ReleaseChannel = "internal" };

        // Act
        string? result = CliContext.ResolveChannel(null, entry);

        // Assert
        Assert.That(result, Is.EqualTo("internal"));
    }

    [Test]
    public void ResolveChannel_BlankExplicitChannel_FallsBackToInstall(
        [Values("", "   ")] string explicitChannel)
    {
        // Arrange
        Ksp2InstallEntry entry = new() { ReleaseChannel = "internal" };

        // Act
        string? result = CliContext.ResolveChannel(explicitChannel, entry);

        // Assert
        Assert.That(result, Is.EqualTo("internal"));
    }

    [Test]
    public void ResolveChannel_NoInstallAndNoChannel_ReturnsNull()
    {
        // Act
        string? result = CliContext.ResolveChannel(null, null);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveChannel_PaddedExplicitChannel_IsTrimmed()
    {
        // Act
        string? result = CliContext.ResolveChannel("  beta  ", null);

        // Assert
        Assert.That(result, Is.EqualTo("beta"));
    }
}
