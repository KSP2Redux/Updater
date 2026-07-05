using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ksp2Redux.Tools.Launcher.Services;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public interface ILogService
{
    void Info(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "");
    void Warn(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "");
    void Error(string message, Exception? exception = null, [CallerFilePath] string source = "", [CallerMemberName] string member = "");
    void Debug(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "");
    string? CurrentLogFilePath { get; }

    /// <summary>
    /// The lowest level that will actually be written. Defaults to <see cref="Services.LogLevel.Info"/>;
    /// lower it to <see cref="Services.LogLevel.Debug"/> for troubleshooting a specific session.
    /// </summary>
    LogLevel MinimumLevel { get; set; }
}

public sealed class LogService : ILogService, IDisposable
{
    private const int MaxLogFilesToKeep = 10;
    private const long DefaultMaxLogFileSizeBytes = 20 * 1024 * 1024;
    private const string LogFilePrefix = "launcher-";
    private const string LogFileExtension = ".log";

    private readonly IFileSystem _fileSystem;
    private readonly long _maxLogFileSizeBytes;
    private readonly object _writeLock = new();
    private StreamWriter? _writer;
    private bool _disposed;
    private bool _sizeCapReached;
    private readonly int _processId;

    public string? CurrentLogFilePath { get; private set; }
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public LogService(IFileSystem fileSystem, IEnvironmentProvider environmentProvider, long maxLogFileSizeBytes = DefaultMaxLogFileSizeBytes)
    {
        _fileSystem = fileSystem;
        _maxLogFileSizeBytes = maxLogFileSizeBytes;
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
        => Write(LogLevel.Info, "INFO", message, null, source, member);

    public void Warn(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "")
        => Write(LogLevel.Warn, "WARN", message, null, source, member);

    public void Error(string message, Exception? exception = null, [CallerFilePath] string source = "", [CallerMemberName] string member = "")
        => Write(LogLevel.Error, "ERROR", message, exception, source, member);

    public void Debug(string message, [CallerFilePath] string source = "", [CallerMemberName] string member = "")
        => Write(LogLevel.Debug, "DEBUG", message, null, source, member);

    private void Write(LogLevel level, string levelName, string message, Exception? exception, string source, string member)
    {
        if (_disposed || level < MinimumLevel) return;

        var sourceName = string.IsNullOrEmpty(source)
            ? ""
            : _fileSystem.Path.GetFileNameWithoutExtension(source);

        var prefix = string.IsNullOrEmpty(sourceName)
            ? $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}] [{levelName}]"
            : $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}] [{levelName}] [{sourceName}.{member}]";

        var line = $"{prefix} {message}";

        lock (_writeLock)
        {
            try
            {
                if (!_sizeCapReached && CurrentLogFilePath is not null &&
                    _fileSystem.FileInfo.New(CurrentLogFilePath).Length >= _maxLogFileSizeBytes)
                {
                    _sizeCapReached = true;
                    _writer?.WriteLine($"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}] [WARN] Log file reached {_maxLogFileSizeBytes} bytes, no further lines will be written to disk this session.");
                }
                if (!_sizeCapReached)
                {
                    _writer?.WriteLine(line);
                    if (exception != null) _writer?.WriteLine(exception.ToString());
                }
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

    private const long MaxBootstrapLogSizeBytes = 1 * 1024 * 1024;

    /// <summary>
    /// Best-effort log used before the DI container is available (Program.cs update-stage error handling).
    /// Appends to a bootstrap log file shared across every launch, so entries are tagged with the process id
    /// to tell separate sessions apart, and the file is reset once it grows past a small cap.
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
            if (File.Exists(path) && new FileInfo(path).Length >= MaxBootstrapLogSizeBytes)
            {
                File.Delete(path);
            }
            var line = $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}] [EARLY] [pid={Environment.ProcessId}] {message}{Environment.NewLine}";
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
