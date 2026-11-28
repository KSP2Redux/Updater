using Testably.Abstractions;

namespace Ksp2Redux.Tools.Launcher.Tests.Transactions;

internal sealed class NativeLinuxFixture : IDisposable
{
    private readonly bool _temporary;

    internal NativeLinuxFixture(string suffix = "", string? temporaryParent = null)
    {
        string? requestedRoot = temporaryParent is null
            ? SystemEnvironmentProvider.Instance.GetEnvironmentVariable("KSP2REDUX_LINUX_FIXTURE_ROOT")
            : null;
        _temporary = string.IsNullOrWhiteSpace(requestedRoot);
        Root = _temporary
            ? FileSystem.Path.Combine(temporaryParent ?? FileSystem.Path.GetTempPath(), $"redux-native-linux-{Guid.NewGuid():N}{suffix}")
            : requestedRoot + suffix;
        if (Root != FileSystem.Path.GetFullPath(Root) || FileSystem.Path.Exists(Root) ||
            (!_temporary && !Root.StartsWith("/home/", StringComparison.Ordinal)))
            throw new InvalidDataException("The native Linux fixture requires a fresh canonical root.");

        FileSystem.Directory.CreateDirectory(Root);
    }

    internal RealFileSystem FileSystem { get; } = new();
    internal string Root { get; }

    public void Dispose()
    {
        // The maintained fixture runner collects proof files from explicitly requested roots.
        if (_temporary)
            FileSystem.Directory.Delete(Root, recursive: true);
    }
}
