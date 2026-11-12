using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Cli.Infrastructure;

/// <summary>
/// Tells the user when a newer CLI is published, without asking GitHub on every run.
/// </summary>
// The answer is cached in the launcher's storage folder rather than in the launcher config, because
// that file belongs to the launcher window and anything the CLI adds to it is dropped the next time
// the launcher saves.
public static class CliUpdateNotice
{
    private const string STATE_FILE = "cli-update-check.json";
    private const int CHECK_INTERVAL_HOURS = 24;
    private const int TIMEOUT_SECONDS = 5;

    /// <summary>
    /// Reports a newer release on stderr, checking GitHub at most once a day.
    /// </summary>
    /// <param name="context">The setup every command shares.</param>
    /// <param name="isEnabled">False when the caller asked for no update check.</param>
    /// <param name="cancellationToken">Token cancelled when the user interrupts the process.</param>
    /// <returns>A task that completes once the notice has been written, if there was one.</returns>
    public static async Task NotifyAsync(CliContext context, bool isEnabled, CancellationToken cancellationToken)
    {
        // Only a person reading a terminal gets this. A script parsing the output, a quiet run and a
        // redirected stderr all stay exactly as they were.
        if (!isEnabled
            || context.Output.IsJson
            || !context.Output.Capabilities.FancyProgress
            || !context.Output.Capabilities.ShowProgress)
        {
            return;
        }

        try
        {
            var state = Read(context);
            var known = await CurrentAsync(context, state, cancellationToken);

            if (known is null || !Version.TryParse(known.LatestVersion, out var latest) || latest <= context.RunningVersion)
            {
                return;
            }

            context.Output.Detail($"A newer CLI is available: {latest}. Run 'redux-launcher-cli self-update' to get it.");
        }
        catch (Exception e)
        {
            context.LogService.Warn($"The update check could not run: {e.Message}");
        }
    }

    private static async Task<CheckState?> CurrentAsync(CliContext context, CheckState? state, CancellationToken cancellationToken)
    {
        if (state is not null && DateTimeOffset.UtcNow - state.CheckedAt < TimeSpan.FromHours(CHECK_INTERVAL_HOURS))
        {
            return state;
        }

        var release = await context.CreateReleaseService(TimeSpan.FromSeconds(TIMEOUT_SECONDS))
            .FindLatestAsync(cancellationToken);

        CheckState refreshed = new()
        {
            CheckedAt = DateTimeOffset.UtcNow,
            LatestVersion = release?.Version.ToString(),
        };

        Write(context, refreshed);
        return refreshed;
    }

    private static CheckState? Read(CliContext context)
    {
        try
        {
            var path = StatePath(context);
            return context.FileSystem.File.Exists(path)
                ? JsonSerializer.Deserialize<CheckState>(context.FileSystem.File.ReadAllText(path))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Write(CliContext context, CheckState state)
    {
        try
        {
            var path = StatePath(context);
            context.FileSystem.Directory.CreateDirectory(context.ConfigService.GetLocalStorageDirectory());
            context.FileSystem.File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch (Exception e)
        {
            context.LogService.Warn($"Could not record when the update check last ran: {e.Message}");
        }
    }

    private static string StatePath(CliContext context) =>
        context.FileSystem.Path.Combine(context.ConfigService.GetLocalStorageDirectory(), STATE_FILE);

    private sealed class CheckState
    {
        [JsonPropertyName("checkedAt")] public DateTimeOffset CheckedAt { get; set; }

        [JsonPropertyName("latestVersion")] public string? LatestVersion { get; set; }
    }
}
