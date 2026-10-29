using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Finds game files that another program holds open, such as a running KSP2 or a Unity editor that loaded the
/// game's bundles.
/// </summary>
public interface IInstallFileLockService
{
    /// <summary>
    /// Throws <see cref="FilesInUseException"/> when any file in the KSP2 folder is held open by another program.
    /// </summary>
    /// <param name="install">The KSP2 folder.</param>
    void EnsureNoFilesInUse(string install);
}

/// <inheritdoc />
public class InstallFileLockService(IFileSystem fileSystem, IOperatingSystemService operatingSystemService) : IInstallFileLockService
{
    // Patching, rollback and bundle conversion never touch these, so a program using them does not matter.
    private static readonly string[] SkippedFolders = ["mods", "UninstallTemp"];

    private const int SHOWN_FILE_COUNT = 5;
    private const int MAX_PARALLEL_CHECKS = 8;

    // A virus scanner or the Windows search indexer can hold a file for a moment, so a held file gets a second look.
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public void EnsureNoFilesInUse(string install)
    {
        var held = FindHeldFiles(EnumerateGameFiles(install));
        if (held.Count > 0)
        {
            Thread.Sleep(RecheckDelay);
            held = FindHeldFiles(held);
        }

        if (held.Count == 0)
        {
            return;
        }

        var programs = FindProgramsHolding(held);
        var relative = held.Select(file => fileSystem.Path.GetRelativePath(install, file)).ToList();
        throw DescribeHeldFiles(relative, programs).ToException(message =>
            new FilesInUseException(message, relative, programs.Select(program => program.English).ToList()));
    }

    private IEnumerable<string> EnumerateGameFiles(string install)
    {
        foreach (var file in fileSystem.Directory.EnumerateFiles(install))
        {
            yield return file;
        }

        foreach (var folder in fileSystem.Directory.EnumerateDirectories(install))
        {
            if (SkippedFolders.Contains(fileSystem.Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in fileSystem.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private List<string> FindHeldFiles(IEnumerable<string> files)
    {
        ConcurrentBag<string> held = [];

        // A virus scanner checks each file the first time it is opened, which made a one-at-a-time pass over a
        // cold install take half a minute. The checks are independent, so they run side by side.
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = MAX_PARALLEL_CHECKS }, file =>
        {
            try
            {
                // Read access needs no write permission, and refusing to share fails while anyone else has it open.
                using var stream = fileSystem.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                held.Add(file);
            }
            catch (UnauthorizedAccessException)
            {
                // A permission problem, not another program. The install reports it with the file it fails on.
            }
        });

        return held.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static LogMessage DescribeHeldFiles(List<string> files, IReadOnlyList<LogMessage> programs)
    {
        LogMessage? heldBy = programs.Count > 0
            ? new LogMessage(string.Join(", ", programs.Select(program => program.English)),
                string.Join(", ", programs.Select(program => program.Localized)))
            : null;
        var cause = (files.Count == 1, heldBy) switch
        {
            (true, null) => Loc.Message("Locks.OneFile.OtherProgram"),
            (true, _) => Loc.Message("Locks.OneFile.Programs", heldBy),
            (false, null) => Loc.Message("Locks.ManyFiles.OtherProgram", files.Count),
            _ => Loc.Message("Locks.ManyFiles.Programs", files.Count, heldBy)
        };
        var shown = string.Join(", ", files.Take(SHOWN_FILE_COUNT));
        var inUse = files.Count > SHOWN_FILE_COUNT
            ? Loc.Message("Locks.InUseMore", shown, files.Count - SHOWN_FILE_COUNT)
            : Loc.Message("Locks.InUse", shown);
        var advice = Loc.Message("Locks.Advice");

        return new LogMessage($"{cause.English} {advice.English}\n{inUse.English}",
            $"{cause.Localized} {advice.Localized}\n{inUse.Localized}");
    }

    private static ILocalizationService Loc => LocalizationService.Default;

    private IReadOnlyList<LogMessage> FindProgramsHolding(List<string> files)
    {
        if (!operatingSystemService.IsWindows())
        {
            return [];
        }

        try
        {
            return RestartManager.FindProgramsHolding(files);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return [];
        }
    }

    // The Windows Restart Manager names the processes holding a set of files. It is what Windows Explorer uses to
    // say which program has a file open.
    private static class RestartManager
    {
        private const int ERROR_MORE_DATA = 234;
        private const int CCH_RM_SESSION_KEY = 32;
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int ProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string AppName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string ServiceShortName;

            public int ApplicationType;
            public uint AppStatus;
            public uint TsSessionId;

            [MarshalAs(UnmanagedType.Bool)]
            public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint sessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] fileNames,
            uint applicationCount, RmUniqueProcess[]? applications, uint serviceCount, string[]? serviceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint sessionHandle, out uint processInfoNeeded, ref uint processInfoCount,
            [In, Out] RmProcessInfo[]? affectedApps, ref uint rebootReasons);

        public static IReadOnlyList<LogMessage> FindProgramsHolding(List<string> files)
        {
            if (RmStartSession(out var session, 0, new StringBuilder(CCH_RM_SESSION_KEY + 1)) != 0)
            {
                return [];
            }

            try
            {
                if (RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, null, 0, null) != 0)
                {
                    return [];
                }

                uint count = 0;
                uint rebootReasons = 0;
                var result = RmGetList(session, out var needed, ref count, null, ref rebootReasons);
                if (result != ERROR_MORE_DATA || needed == 0)
                {
                    return [];
                }

                var processes = new RmProcessInfo[needed];
                count = needed;
                if (RmGetList(session, out _, ref count, processes, ref rebootReasons) != 0)
                {
                    return [];
                }

                return processes
                    .Take((int)count)
                    .Select(process => Loc.Message("Locks.Process", process.AppName, process.Process.ProcessId))
                    .Distinct()
                    .ToList();
            }
            finally
            {
                RmEndSession(session);
            }
        }
    }
}