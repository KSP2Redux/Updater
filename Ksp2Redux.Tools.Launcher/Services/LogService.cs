using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface ILogService
{
    void Info(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "");
    void Warn(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "");
    void Error(string message, Exception? exception = null, [CallerFilePath] string source = "", [CallerMemberName] string member = "");
    void Debug(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "");
    string? CurrentLogFilePath { get; }
}

public sealed class LogService : ILogService, IDisposable
{
    private const int MaxLogFilesToKeep = 10;
    private const string LogFilePrefix = "launcher-";
    private const string LogFileExtension = ".log";

    private readonly IFileSystem _fileSystem;
    private readonly object _writeLock = new();
    private StreamWriter? _writer;
    private bool _disposed;
    private readonly int _processId;

    public string? CurrentLogFilePath { get; private set; }

    public LogService(IFileSystem fileSystem, IEnvironmentProvider environmentProvider)
    {
        _fileSystem = fileSystem;
        _processId = environmentProvider.ProcessId;
        try
        {
            var logsDir = LocalStoragePaths.GetLogsDirectory(fileSystem, environmentProvider);
            fileSystem.Directory.CreateDirectory(logsDir);
            RotateOldLogs(logsDir);

            var fileName = LogFilePrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + LogFileExtension;
            CurrentLogFilePath = fileSystem.Path.Combine(logsDir, fileName);

            var stream = fileSystem.FileStream.New(CurrentLogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            WriteHeader();
        }
        catch (Exception ex)
        {
            _writer = null;
            CurrentLogFilePath = null;
            Console.WriteLine($"[LogService] Failed to open log file, falling back to console only: {ex.Message}");
        }
    }

    private void WriteHeader()
    {
        var asm = typeof(LogService).Assembly.GetName();
        var header = new StringBuilder();
        header.AppendLine("===== Ksp2Redux Launcher Log =====");
        header.AppendLine($"Started: {DateTime.Now:O}");
        header.AppendLine($"Version: {asm.Version}");
        header.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        header.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        header.AppendLine($"Process: {_processId}");
        header.AppendLine("==================================");
        lock (_writeLock)
        {
            _writer?.Write(header.ToString());
        }
        Console.Write(header.ToString());
    }

    private void RotateOldLogs(string logsDir)
    {
        try
        {
            var existing = _fileSystem.Directory
                .EnumerateFiles(logsDir, LogFilePrefix + "*" + LogFileExtension)
                .Select(p => new { Path = p, Info = _fileSystem.FileInfo.New(p) })
                .OrderByDescending(f => f.Info.LastWriteTimeUtc)
                .Skip(MaxLogFilesToKeep - 1)
                .ToList();

            foreach (var stale in existing)
            {
                try { _fileSystem.File.Delete(stale.Path); }
                catch { }
            }
        }
        catch { }
    }

    public void Info(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "")
        => Write("INFO", message, null, source, member);

    public void Warn(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "")
        => Write("WARN", message, null, source, member);

    public void Error(string message, Exception? exception = null, [CallerFilePath] string source = "", [CallerMemberName] string member = "")
        => Write("ERROR", message, exception, source, member);

    public void Debug(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "")
        => Write("DEBUG", message, null, source, member);

    private void Write(string level, string message, Exception? exception, string source, string member)
    {
        if (_disposed) return;

        var sourceName = string.IsNullOrEmpty(source)
            ? ""
            : _fileSystem.Path.GetFileNameWithoutExtension(source);

        var prefix = string.IsNullOrEmpty(sourceName)
            ? $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}] [{level}]"
            : $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}] [{level}] [{sourceName}.{member}]";

        var line = $"{prefix} {message}";

        lock (_writeLock)
        {
            try
            {
                _writer?.WriteLine(line);
                if (exception != null) _writer?.WriteLine(exception.ToString());
            }
            catch { }
        }
        Console.WriteLine(line);
        if (exception != null) Console.WriteLine(exception.ToString());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_writeLock)
        {
            try { _writer?.Flush(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    /// <summary>
    /// Best-effort log used before the DI container is available (Program.cs update-stage error handling).
    /// Appends to a bootstrap log file in the same logs directory the regular LogService writes to.
    /// </summary>
    public static void WriteEarly(string message)
    {
#pragma warning disable RS0030
        try
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logsDir = Path.Combine(appdata, LocalStoragePaths.ReduxFolder, LocalStoragePaths.LogsSubfolder);
            Directory.CreateDirectory(logsDir);
            var path = Path.Combine(logsDir, "bootstrap.log");
            var line = $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}] [EARLY] {message}{Environment.NewLine}";
            File.AppendAllText(path, line);
            Console.WriteLine(line.TrimEnd());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LogService.WriteEarly] {message}");
            Console.WriteLine($"[LogService.WriteEarly] write failed: {ex.Message}");
        }
#pragma warning restore RS0030
    }
}
