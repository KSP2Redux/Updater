namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// How much styling the CLI applies to its output.
/// </summary>
public enum ColorMode
{
    /// <summary>
    /// Style a stream when it is attached to a terminal, and leave it plain when it is redirected.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Style every stream, even a redirected one.
    /// </summary>
    Always = 1,

    /// <summary>
    /// Leave every stream plain, even an interactive one.
    /// </summary>
    Never = 2,
}
