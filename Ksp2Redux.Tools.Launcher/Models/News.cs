using System;

namespace Ksp2Redux.Tools.Launcher.Models;

public sealed record News
{
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Link { get; init; } = string.Empty;
}