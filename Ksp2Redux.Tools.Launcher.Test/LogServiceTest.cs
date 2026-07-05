using System.IO.Abstractions;
using Ksp2Redux.Tools.Launcher.Services;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Test;

public class LogServiceTest
{
    private const string AppData = @"C:\appdata";
    private const string LogsDir = @"C:\appdata\Ksp2Redux\logs";

    private static (MockFileSystem fs, MockEnvironmentProvider env) BuildEnv()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        var env = new MockEnvironmentProvider();
        env.SetFolderPath(Environment.SpecialFolder.LocalApplicationData, AppData);
        return (fs, env);
    }

    [Test]
    public void Constructor_CreatesLogsDirectoryIfMissing()
    {
        var (fs, env) = BuildEnv();
        Assert.That(fs.Directory.Exists(LogsDir), Is.False);

        using var log = new LogService(fs, env);

        Assert.That(fs.Directory.Exists(LogsDir), Is.True);
    }

    [Test]
    public void Constructor_OpensNewLogFileMatchingPattern()
    {
        var (fs, env) = BuildEnv();

        using var log = new LogService(fs, env);

        Assert.That(log.CurrentLogFilePath, Is.Not.Null);
        var files = fs.Directory.EnumerateFiles(LogsDir, "launcher-*.log").ToList();
        Assert.That(files, Has.Count.EqualTo(1));
        Assert.That(files[0], Is.EqualTo(log.CurrentLogFilePath));
    }

    [Test]
    public void Info_WritesFormattedLineToLogFile()
    {
        var (fs, env) = BuildEnv();
        using var log = new LogService(fs, env);

        log.Info("hello world");
        log.Dispose();

        var contents = fs.File.ReadAllText(log.CurrentLogFilePath!);
        Assert.Multiple(() =>
        {
            Assert.That(contents, Does.Contain("[INFO]"));
            Assert.That(contents, Does.Contain("hello world"));
        });
    }

    [Test]
    public void Error_WritesExceptionDetailsToLogFile()
    {
        var (fs, env) = BuildEnv();
        using var log = new LogService(fs, env);

        log.Error("something broke", new InvalidOperationException("bad state"));
        log.Dispose();

        var contents = fs.File.ReadAllText(log.CurrentLogFilePath!);
        Assert.Multiple(() =>
        {
            Assert.That(contents, Does.Contain("[ERROR]"));
            Assert.That(contents, Does.Contain("something broke"));
            Assert.That(contents, Does.Contain("InvalidOperationException"));
            Assert.That(contents, Does.Contain("bad state"));
        });
    }

    [Test]
    public void Constructor_WhenOverFileCap_DeletesOldestSoNewFilePlusNineRemain()
    {
        var (fs, env) = BuildEnv();
        fs.Directory.CreateDirectory(LogsDir);

        // Create 10 existing log files with staggered timestamps so the rotation order is deterministic.
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++)
        {
            var path = fs.Path.Combine(LogsDir, $"launcher-2026010{i % 10}-000000.log");
            fs.File.WriteAllText(path, $"old {i}");
            fs.File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(i));
        }

        using var log = new LogService(fs, env);

        var remaining = fs.Directory.EnumerateFiles(LogsDir, "launcher-*.log").ToList();
        Assert.That(remaining, Has.Count.EqualTo(10), "Should keep at most 10 log files including the new one.");
        Assert.That(remaining, Does.Contain(log.CurrentLogFilePath));
    }

    [Test]
    public void Write_LogFileExceedsSizeCap_StopsWritingFurtherLinesToDisk()
    {
        var (fs, env) = BuildEnv();
        var log = new LogService(fs, env, maxLogFileSizeBytes: 200);

        // Push the file past the tiny test cap, then keep writing.
        log.Info(new string('x', 250));
        var sizeAfterCapHit = fs.FileInfo.New(log.CurrentLogFilePath!).Length;

        log.Info("this line must not reach disk");
        log.Info("neither must this one");

        var sizeAfterMoreWrites = fs.FileInfo.New(log.CurrentLogFilePath!).Length;
        log.Dispose();
        var contents = fs.File.ReadAllText(log.CurrentLogFilePath!);
        Assert.Multiple(() =>
        {
            Assert.That(sizeAfterMoreWrites, Is.EqualTo(sizeAfterCapHit), "No further lines should be written once the cap is reached.");
            Assert.That(contents, Does.Contain("Log file reached"));
            Assert.That(contents, Does.Not.Contain("this line must not reach disk"));
        });
    }

    [Test]
    public void Debug_BelowDefaultMinimumLevel_IsNotWrittenToLogFile()
    {
        var (fs, env) = BuildEnv();
        using var log = new LogService(fs, env);

        log.Debug("verbose detail");
        log.Info("normal message");
        log.Dispose();

        var contents = fs.File.ReadAllText(log.CurrentLogFilePath!);
        Assert.Multiple(() =>
        {
            Assert.That(contents, Does.Not.Contain("verbose detail"));
            Assert.That(contents, Does.Contain("normal message"));
        });
    }

    [Test]
    public void Debug_MinimumLevelLoweredToDebug_IsWrittenToLogFile()
    {
        var (fs, env) = BuildEnv();
        using var log = new LogService(fs, env) { MinimumLevel = LogLevel.Debug };

        log.Debug("verbose detail");
        log.Dispose();

        var contents = fs.File.ReadAllText(log.CurrentLogFilePath!);
        Assert.That(contents, Does.Contain("verbose detail"));
    }

    [Test]
    public void Constructor_WhenFileOpenFails_FallsBackToConsoleOnly()
    {
        var env = new MockEnvironmentProvider();
        env.SetFolderPath(Environment.SpecialFolder.LocalApplicationData, AppData);

        var fs = new Mock<IFileSystem>();
        var path = new Mock<IPath>();
        var directory = new Mock<IDirectory>();
        path.Setup(p => p.Combine(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((a, b) => a + "\\" + b);
        directory.Setup(d => d.CreateDirectory(It.IsAny<string>()))
            .Throws(new UnauthorizedAccessException("simulated permission failure"));
        fs.SetupGet(f => f.Path).Returns(path.Object);
        fs.SetupGet(f => f.Directory).Returns(directory.Object);

        Assert.DoesNotThrow(() =>
        {
            using var log = new LogService(fs.Object, env);
            log.Info("should not throw");
            log.Warn("should not throw");
            log.Error("should not throw");
            Assert.That(log.CurrentLogFilePath, Is.Null);
        });
    }
}
