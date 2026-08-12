namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// The token every command watches, cancelled when the user interrupts the process.
/// </summary>
public static class CliCancellation
{
    private static readonly CancellationTokenSource SOURCE = new();

    /// <summary>
    /// Gets the token cancelled on Ctrl+C.
    /// </summary>
    public static CancellationToken Token => SOURCE.Token;

    /// <summary>
    /// Hooks Ctrl+C so it cancels the running command rather than killing the process.
    /// </summary>
    public static void CancelOnInterrupt()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            SOURCE.Cancel();
        };
    }
}
