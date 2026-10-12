using System.IO.Abstractions;
using Ksp2Redux.Tools.BundleConversion;

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
public class BundleConversionService(IFileSystem fileSystem) : IBundleConversionService
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
        var result = converter.Apply(manifest, false, ct);
        log($"Bundle conversion: {result.EditsWritten} edits written, {result.EditsAlreadyApplied} already applied, " +
            $"{result.CopiesWritten} bundle copies written, {result.CopiesUpToDate} up to date, {result.CopiesDeleted} removed.");
        foreach (var edit in result.ForeignEdits)
        {
            log($"Left alone, the bundle holds unexpected bytes: {edit}");
        }

        foreach (var failure in result.Failures)
        {
            log($"Could not convert: {failure}");
        }

        ThrowIfAnyFailed(result.Failures.Count, "converted");
    }

    /// <inheritdoc />
    public void Revert(string install, Action<string> log, CancellationToken ct)
    {
        var converter = new BundleConverter(fileSystem, install, new BundleConversionOptions { Progress = new Progress<string>(log) });
        var result = converter.Revert(ct);
        if (!result.HadJournal)
        {
            return;
        }

        log($"Bundle conversion reverted: {result.EditsReverted} edits undone, {result.CopiesDeleted} bundle copies removed.");
        foreach (var edit in result.Unreverted)
        {
            log($"Could not undo, the bundle holds unexpected bytes: {edit}");
        }

        foreach (var failure in result.Failures)
        {
            log($"Could not revert: {failure}");
        }

        ThrowIfAnyFailed(result.Failures.Count, "put back to stock");
    }

    // A bundle the conversion could not write stays as it was while the rest move on, so the caller has to treat
    // the step as failed rather than carry on with a mixed set.
    private static void ThrowIfAnyFailed(int failureCount, string action)
    {
        if (failureCount > 0)
        {
            throw new IOException(failureCount == 1
                ? $"A stock bundle could not be {action}. Another program may have it open."
                : $"{failureCount} stock bundles could not be {action}. Another program may have them open.");
        }
    }
}