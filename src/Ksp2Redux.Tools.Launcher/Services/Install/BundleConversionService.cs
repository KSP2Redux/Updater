using System.IO.Abstractions;
using Ksp2Redux.Tools.BundleConversion;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Steam;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

/// <summary>
/// Converts and reverts an install's stock bundles so their materials use the URP replacement shaders.
/// </summary>
public interface IBundleConversionService
{
    /// <summary>
    /// Converts the stock bundles against the shader manifest the installed Redux build ships. An install
    /// without one (a build from before the URP port) has any earlier conversion reverted instead.
    /// </summary>
    /// <param name="install">The KSP2 folder.</param>
    /// <param name="log">Receives progress and summary lines.</param>
    /// <param name="ct">Cancels the conversion between bundles.</param>
    /// <exception cref="IOException">A bundle could not be converted, after the others were.</exception>
    void Convert(string install, Action<LogMessage> log, CancellationToken ct);

    /// <summary>
    /// Puts every converted bundle back to stock from the conversion journal, if the install has one.
    /// </summary>
    /// <param name="install">The KSP2 folder.</param>
    /// <param name="log">Receives progress and summary lines.</param>
    /// <param name="ct">Cancels the revert between bundles.</param>
    /// <exception cref="IOException">A bundle could not be put back, after the others were. The journal keeps it.</exception>
    void Revert(string install, Action<LogMessage> log, CancellationToken ct);
}

/// <inheritdoc />
// Stock bundles can hold converted shader references no journal records, left by earlier launchers or by a
// journal lost along the way. No revert can undo those, and a build without the replacement shaders draws them
// magenta, so both directions look for them and put Steam's copies of those bundles back.
public class BundleConversionService(
    IFileSystem fileSystem,
    ISteamSessionService? steamSession = null,
    ISteamDepotDownloader? steamDownloader = null) : IBundleConversionService
{
    /// <summary>
    /// The URP replacement shader bundle manifest, relative to the KSP2 folder.
    /// </summary>
    public const string SHADER_MANIFEST = "Redux/ShaderBundles/StandaloneWindows64/manifest.json";

    /// <inheritdoc />
    public void Convert(string install, Action<LogMessage> log, CancellationToken ct)
    {
        var manifestPath = fileSystem.Path.Combine(install, SHADER_MANIFEST);
        if (!fileSystem.File.Exists(manifestPath))
        {
            log(Loc.Message("Bundles.NoShaderBundles"));
            Revert(install, log, ct);
            return;
        }

        var manifest = ShaderManifestIndex.Load(fileSystem, manifestPath);
        foreach (var warning in manifest.Warnings)
        {
            log(Loc.Message("Bundles.ManifestWarning", warning));
        }

        log(Loc.Message("Bundles.Converting"));
        var converter = new BundleConverter(fileSystem, install, new BundleConversionOptions { Progress = ConverterProgress(log) });
        var unrecorded = Apply(converter, manifest, log, ct);
        if (unrecorded.Count == 0)
        {
            return;
        }

        // The URP build draws these bundles fine, but nothing could ever take them back to stock. Stock copies
        // get converted again, and this time journaled. Without Steam this install still works, and the next
        // revert, where the edits start to matter, tries again and stops if it cannot.
        try
        {
            RestoreFromSteam(install, unrecorded, log, ct);
        }
        catch (IOException e)
        {
            log(Loc.Message("Bundles.Warning", e.ToLogMessage()));
            return;
        }

        log(Loc.Message("Bundles.ConvertingRestored"));
        unrecorded = Apply(converter, manifest, log, ct);
        if (unrecorded.Count > 0)
        {
            throw Loc.Message("Bundles.StillUnrecorded", unrecorded.Count, unrecorded[0])
                .ToException(message => new IOException(message));
        }
    }

    /// <inheritdoc />
    public void Revert(string install, Action<LogMessage> log, CancellationToken ct)
    {
        var converter = new BundleConverter(fileSystem, install, new BundleConversionOptions { Progress = ConverterProgress(log) });
        var result = RevertJournal(converter, log, ct);

        // Without a journal there may still be converted bundles, from a launcher that lost or never kept it.
        var broken = result.Unreverted.Select(edit => edit.File)
            .Concat(converter.FindUnrecordedEdits(ct))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
        if (broken.Count == 0)
        {
            return;
        }

        RestoreFromSteam(install, broken, log, ct);
        if (result.Unreverted.Count == 0)
        {
            return;
        }

        // The journal kept the edits it could not undo. Their bundles are stock now, so this clears it.
        result = RevertJournal(converter, log, ct);
        if (result.Unreverted.Count > 0)
        {
            throw Loc.Message("Bundles.StillUnreverted", result.Unreverted.Count, result.Unreverted[0].File)
                .ToException(message => new IOException(message));
        }
    }

    // The conversion lines reach the install log from static and instance members alike, so they read the shared service.
    private static ILocalizationService Loc => LocalizationService.Default;

    // The converter reports plain text, which is never translated.
    private static IProgress<string> ConverterProgress(Action<LogMessage> log) =>
        new Progress<string>(text => log(LogMessage.Literal(text)));

    // Returns the bundles holding converted shaders the journal does not record.
    private static IReadOnlyList<string> Apply(BundleConverter converter, ShaderManifestIndex manifest, Action<LogMessage> log, CancellationToken ct)
    {
        var result = converter.Apply(manifest, false, ct);
        log(Loc.Message("Bundles.ApplySummary", result.EditsWritten, result.EditsAlreadyApplied, result.CopiesWritten,
            result.CopiesUpToDate, result.CopiesDeleted, result.OverlaysWritten, result.OverlaysUpToDate, result.OverlaysDeleted));
        foreach (var edit in result.ForeignEdits)
        {
            log(Loc.Message("Bundles.LeftAlone", edit.File, edit.Offset));
        }

        foreach (var failure in result.TextureFailures)
        {
            log(Loc.Message("Bundles.KeptCloudTextures", failure.File, failure.Message));
        }

        foreach (var failure in result.Failures)
        {
            log(Loc.Message("Bundles.ConvertFailed", failure.File, failure.Message));
        }

        ThrowIfAnyFailed(result.Failures, "Bundles.ConvertFailedOne", "Bundles.ConvertFailedMany");
        return result.Plan.UnrecordedEdits;
    }

    private static RevertResult RevertJournal(BundleConverter converter, Action<LogMessage> log, CancellationToken ct)
    {
        var result = converter.Revert(ct);
        if (!result.HadJournal)
        {
            return result;
        }

        log(Loc.Message("Bundles.RevertSummary", result.EditsReverted, result.CopiesDeleted, result.OverlaysDeleted));
        foreach (var edit in result.Unreverted)
        {
            log(Loc.Message("Bundles.CouldNotUndo", edit.File, edit.Offset));
        }

        foreach (var failure in result.Failures)
        {
            log(Loc.Message("Bundles.RevertFailed", failure.File, failure.Message));
        }

        ThrowIfAnyFailed(result.Failures, "Bundles.RevertFailedOne", "Bundles.RevertFailedMany");
        return result;
    }

    // Steam's copies are the only stock bytes there are for an edit nobody recorded.
    private void RestoreFromSteam(string install, IReadOnlyList<string> bundles, Action<LogMessage> log, CancellationToken ct)
    {
        log(Loc.Message("Bundles.Unrecorded", bundles.Count, string.Join(", ", bundles)));
        var help = Loc.Message("Bundles.SteamHelp");
        if (steamSession is null || steamDownloader is null || !steamSession.HasSavedLogin)
        {
            throw Loc.Message("Bundles.NeedSteamCopies", bundles.Count, help).ToException(message => new IOException(message));
        }

        if (!steamSession.IsSignedIn && !steamSession.TryResumeAsync(ct).GetAwaiter().GetResult())
        {
            throw Loc.Message("Bundles.SteamLoginRejected", bundles.Count, help).ToException(message => new IOException(message));
        }

        log(Loc.Message("Bundles.DownloadingFromSteam"));
        try
        {
            steamDownloader.RestoreFilesAsync(
                SteamDepotDownloader.KSP2_APP_ID,
                install,
                [.. bundles.Select(file => BundleConversionLayout.BUNDLE_ROOT + "/" + file)],
                new Progress<SteamDownloadProgress>(),
                ct).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is SteamSignInException or InvalidDataException)
        {
            throw Loc.Message("Bundles.SteamDownloadFailed", bundles.Count, e.ToLogMessage(), help)
                .ToException(message => new IOException(message, e));
        }

        log(Loc.Message("Bundles.PutBack", bundles.Count));
    }

    // A bundle the conversion could not write stays as it was while the rest move on, so the caller has to treat
    // the step as failed rather than carry on with a mixed set.
    private static void ThrowIfAnyFailed(IReadOnlyList<BundleProblem> failures, string oneKey, string manyKey)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var first = failures[0];
        var message = failures.Count == 1
            ? Loc.Message(oneKey, first.File, first.Message)
            : Loc.Message(manyKey, failures.Count, first.File, first.Message);
        throw message.ToException(text => new IOException(text));
    }
}