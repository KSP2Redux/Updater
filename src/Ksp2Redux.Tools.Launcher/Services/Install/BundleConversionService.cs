using System.IO.Abstractions;
using Ksp2Redux.Tools.BundleConversion;
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
    void Convert(string install, Action<string> log, CancellationToken ct);

    /// <summary>
    /// Puts every converted bundle back to stock from the conversion journal, if the install has one.
    /// </summary>
    /// <param name="install">The KSP2 folder.</param>
    /// <param name="log">Receives progress and summary lines.</param>
    /// <param name="ct">Cancels the revert between bundles.</param>
    /// <exception cref="IOException">A bundle could not be put back, after the others were. The journal keeps it.</exception>
    void Revert(string install, Action<string> log, CancellationToken ct);
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
    public void Convert(string install, Action<string> log, CancellationToken ct)
    {
        var manifestPath = fileSystem.Path.Combine(install, SHADER_MANIFEST);
        if (!fileSystem.File.Exists(manifestPath))
        {
            log("This Redux build has no URP shader bundles, so the stock bundles stay unconverted.");
            Revert(install, log, ct);
            return;
        }

        var manifest = ShaderManifestIndex.Load(fileSystem, manifestPath);
        foreach (var warning in manifest.Warnings)
        {
            log($"Shader manifest: {warning}");
        }

        log("Converting the stock bundles to the URP shaders...");
        var converter = new BundleConverter(fileSystem, install, new BundleConversionOptions { Progress = new Progress<string>(log) });
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
            log($"Warning: {e.Message}");
            return;
        }

        log("Converting the bundles restored from Steam...");
        unrecorded = Apply(converter, manifest, log, ct);
        if (unrecorded.Count > 0)
        {
            throw new IOException($"{unrecorded.Count} stock bundles still hold unrecorded converted shaders after Steam's copies were put back, the first is {unrecorded[0]}.");
        }
    }

    /// <inheritdoc />
    public void Revert(string install, Action<string> log, CancellationToken ct)
    {
        var converter = new BundleConverter(fileSystem, install, new BundleConversionOptions { Progress = new Progress<string>(log) });
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
            throw new IOException($"{result.Unreverted.Count} converted shader references could not be undone even after Steam's copies were put back, the first in {result.Unreverted[0].File}.");
        }
    }

    // Returns the bundles holding converted shaders the journal does not record.
    private static IReadOnlyList<string> Apply(BundleConverter converter, ShaderManifestIndex manifest, Action<string> log, CancellationToken ct)
    {
        var result = converter.Apply(manifest, false, ct);
        log($"Bundle conversion: {result.EditsWritten} edits written, {result.EditsAlreadyApplied} already applied, " +
            $"{result.CopiesWritten} bundle copies written, {result.CopiesUpToDate} up to date, {result.CopiesDeleted} removed.");
        foreach (var edit in result.ForeignEdits)
        {
            log($"Left alone, {edit.File} holds unexpected bytes at offset {edit.Offset}.");
        }

        foreach (var failure in result.Failures)
        {
            log($"Could not convert {failure.File}: {failure.Message}");
        }

        ThrowIfAnyFailed(result.Failures, "converted");
        return result.Plan.UnrecordedEdits;
    }

    private static RevertResult RevertJournal(BundleConverter converter, Action<string> log, CancellationToken ct)
    {
        var result = converter.Revert(ct);
        if (!result.HadJournal)
        {
            return result;
        }

        log($"Bundle conversion reverted: {result.EditsReverted} edits undone, {result.CopiesDeleted} bundle copies removed.");
        foreach (var edit in result.Unreverted)
        {
            log($"Could not undo, {edit.File} holds unexpected bytes at offset {edit.Offset}.");
        }

        foreach (var failure in result.Failures)
        {
            log($"Could not revert {failure.File}: {failure.Message}");
        }

        ThrowIfAnyFailed(result.Failures, "put back to stock");
        return result;
    }

    // Steam's copies are the only stock bytes there are for an edit nobody recorded.
    private void RestoreFromSteam(string install, IReadOnlyList<string> bundles, Action<string> log, CancellationToken ct)
    {
        log($"{bundles.Count} stock bundles hold converted shaders that no conversion record covers: {string.Join(", ", bundles)}.");
        const string HELP = "Sign in with Steam in the launcher's settings (or run 'redux-launcher-cli steam login') and install " +
                            "again, or have Steam verify the game files and then reinstall Redux.";
        if (steamSession is null || steamDownloader is null || !steamSession.HasSavedLogin)
        {
            throw new IOException($"{bundles.Count} stock bundles hold converted shaders that only Steam's copies can put back. {HELP}");
        }

        if (!steamSession.IsSignedIn && !steamSession.TryResumeAsync(ct).GetAwaiter().GetResult())
        {
            throw new IOException($"Steam did not accept the saved login, which is needed to put {bundles.Count} stock bundles back. {HELP}");
        }

        log("Downloading Steam's copies of those bundles...");
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
            throw new IOException($"Steam's copies of {bundles.Count} stock bundles could not be downloaded: {e.Message} {HELP}", e);
        }

        log($"Put back Steam's copies of {bundles.Count} stock bundles.");
    }

    // A bundle the conversion could not write stays as it was while the rest move on, so the caller has to treat
    // the step as failed rather than carry on with a mixed set.
    private static void ThrowIfAnyFailed(IReadOnlyList<BundleProblem> failures, string action)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var first = failures[0];
        throw new IOException(failures.Count == 1
            ? $"A stock bundle could not be {action} ({first.File}: {first.Message})."
            : $"{failures.Count} stock bundles could not be {action}, the first was {first.File} ({first.Message}).");
    }
}