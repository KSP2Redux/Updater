using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Cli.Settings;

namespace Ksp2Redux.Tools.Cli.Commands;

/// <summary>
/// Reports or prunes the finished shader bundles shared by Unity checkouts.
/// </summary>
public sealed class ShaderCacheCommand : ReduxCommand<ShaderCacheSettings>
{
    /// <inheritdoc />
    protected override bool NoticeApplies => false;

    /// <inheritdoc />
    protected override Task<int> RunAsync(CliContext context, ShaderCacheSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShaderBundleCache? cache = ShaderBundleCache.FromEnvironment(context.FileSystem);
        int removed = settings.Prune && cache != null ? cache.Prune() : 0;
        context.Output.Payload(
            new { ok = true, enabled = cache != null, root = cache?.Root, removed },
            () => context.Output.Result(cache == null ? "Shader cache disabled." : $"{cache.Root}: {removed} expired entries removed."));
        return Task.FromResult(ExitCode.SUCCESS);
    }
}
