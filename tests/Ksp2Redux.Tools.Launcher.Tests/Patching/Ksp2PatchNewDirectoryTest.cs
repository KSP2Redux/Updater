using System.IO.Compression;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Patching;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Patching;

/// <summary>
/// Regression coverage for patch generation failing on new directories in CI job 23087.
/// </summary>
public class Ksp2PatchNewDirectoryTest
{
    /// <summary>
    /// Verifies new nested files are included while obsolete files are still removed.
    /// </summary>
    [Test]
    public void DeltaAddsNewDirectoriesAndRetainsRemovalChecks()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(@"C:\Old\Existing");
        fs.Directory.CreateDirectory(@"C:\New\Existing");
        fs.Directory.CreateDirectory(@"C:\New\Added\Nested");
        fs.Directory.CreateDirectory(@"C:\New\Empty");
        fs.File.WriteAllText(@"C:\Old\Existing\obsolete.txt", "obsolete");
        fs.File.WriteAllText(@"C:\New\Added\Nested\asset.txt", "new asset");

        using (Ksp2Patch.FromDiff(fs, @"C:\delta.patch", @"C:\Old", @"C:\New", checkRemovals: true))
        {
        }

        using var stream = new MemoryStream(fs.File.ReadAllBytes(@"C:\delta.patch"));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var operations = manifest.RootElement.GetProperty("operations").EnumerateArray()
            .ToDictionary(operation => operation.GetProperty("fileName").GetString()!,
                operation => (PatchOperation.PatchAction)operation.GetProperty("action").GetInt32());

        Assert.That(operations, Has.Count.EqualTo(2));
        Assert.That(operations[@"Added\Nested\asset.txt"], Is.EqualTo(PatchOperation.PatchAction.Add));
        Assert.That(operations[@"Existing\obsolete.txt"], Is.EqualTo(PatchOperation.PatchAction.Remove));
        using var payload = new StreamReader(archive.GetEntry(@"Added\Nested\asset.txt")!.Open());
        Assert.That(payload.ReadToEnd(), Is.EqualTo("new asset"));
    }

    /// <summary>
    /// Verifies Unity debug archives are excluded from full and incremental patches.
    /// </summary>
    /// <param name="checkRemovals">Whether to generate an incremental patch.</param>
    [TestCase(false)]
    [TestCase(true)]
    public void DebugArchiveIsExcluded(bool checkRemovals)
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        fs.Directory.CreateDirectory(@"C:\Old");
        const string DEBUG_DIRECTORY = "KSP2_x64_BackUpThisFolder_ButDontShipItWithYourGame";
        string symbols = fs.Path.Combine(@"C:\New", DEBUG_DIRECTORY, "Nested", "symbols.pdb");
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(symbols)!);
        fs.File.WriteAllText(symbols, "debug symbols");
        fs.File.WriteAllText(@"C:\New\game.txt", "game");

        using (Ksp2Patch.FromDiff(fs, @"C:\release.patch", @"C:\Old", @"C:\New", checkRemovals))
        {
        }

        using var stream = new MemoryStream(fs.File.ReadAllBytes(@"C:\release.patch"));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        JsonElement operation = manifest.RootElement.GetProperty("operations").EnumerateArray().Single();
        Assert.That(operation.GetProperty("fileName").GetString(), Is.EqualTo("game.txt"));
        Assert.That(archive.Entries.Select(entry => entry.FullName), Is.EquivalentTo(new[] { "game.txt", "manifest.json" }));
    }
}
