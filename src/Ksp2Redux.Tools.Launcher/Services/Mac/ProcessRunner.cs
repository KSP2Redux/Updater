using System.Diagnostics;

namespace Ksp2Redux.Tools.Launcher.Services.Mac;

public interface IProcessRunner
{
    /// <summary>
    /// Runs a process to completion, forwarding each line it prints to <paramref name="log"/>.
    /// Cancelling kills the whole process tree.
    /// </summary>
    /// <returns>The process exit code.</returns>
    Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> log, CancellationToken cancellationToken);
}

public class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> log, CancellationToken cancellationToken)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = new Process();
        process.StartInfo = startInfo;
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
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
