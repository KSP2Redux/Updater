namespace Ksp2Redux.Tools.Launcher.Services.Install;

public sealed class InsufficientDiskSpaceException(string message, string path, long requiredBytes, long availableBytes)
    : InvalidOperationException(message)
{
    public string Path { get; } = path;
    public long RequiredBytes { get; } = requiredBytes;
    public long AvailableBytes { get; } = availableBytes;
}
