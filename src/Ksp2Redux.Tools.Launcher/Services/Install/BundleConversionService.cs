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
    void Convert(string install, Action<string> log, CancellationToken ct);

    /// <summary>
    /// Puts every converted bundle back to stock from the conversion journal, if the install has one.
    /// </summary>
    /// <param name="install">The KSP2 folder.</param>
    /// <param name="log">Receives progress and summary lines.</param>
    /// <param name="ct">Cancels the revert between bundles.</param>
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
    }
}