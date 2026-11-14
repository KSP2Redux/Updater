using System.Diagnostics;
using Ksp2Redux.Tools.Launcher.Localization;

namespace Ksp2Redux.Tools.Launcher.Services.Mac;

public interface IProcessRunner
{
    /// <summary>
    /// Runs a process to completion, forwarding each line it prints to <paramref name="log"/>.
    /// Cancelling kills the whole process tree.
    /// </summary>
    /// <returns>The process exit code.</returns>
    Task<int> RunAsync(ProcessStartInfo startInfo, Action<LogMessage> log, CancellationToken cancellationToken);
}

public class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(ProcessStartInfo startInfo, Action<LogMessage> log, CancellationToken cancellationToken)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo)!;

        try
        {
            await foreach (var line in process.ReadAllLinesAsync(cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(line.Content)) log(LogMessage.Literal(line.Content));
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        return process.ExitCode;
    }
}
