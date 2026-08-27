using System.IO.Compression;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Patching;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Patching;

public class Ksp2PatchScopedRemovalTest
{
    private const string OriginalDir = @"C:\Stock";
    private const string TargetDir = @"C:\Redux";
    private const string PatchPath = @"C:\redux.patch";
    private const string ShapesPath = @"KSP2_x64_Data\Managed\ShapesRuntime.dll";

    [Test]
    public void FromDiff_MissingDllUnderRemovalRoot_EmitsRemoveOperation()
    {
        var fs = new MockFileSystem(o => o.SimulatingOperatingSystem(SimulationMode.Windows));
        string originalShapes = fs.Path.Combine(OriginalDir, ShapesPath);
        string retainedStock = fs.Path.Combine(OriginalDir, "KSP2_x64_Data", "Managed", "Retained.dll");
        string retainedTarget = fs.Path.Combine(TargetDir, "KSP2_x64_Data", "Managed", "Retained.dll");
        string nonDllStock = fs.Path.Combine(OriginalDir, "KSP2_x64_Data", "Managed", "stock-data.json");
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(originalShapes)!);
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(retainedTarget)!);
        fs.File.WriteAllText(originalShapes, "stock shapes");
        fs.File.WriteAllText(retainedStock, "retained");
        fs.File.WriteAllText(retainedTarget, "retained");
        fs.File.WriteAllText(nonDllStock, "stock data");

        using (Ksp2Patch.FromDiff(
                   fs,
                   PatchPath,
                   OriginalDir,
                   TargetDir,
                   checkMissingDllsUnder: [@"KSP2_x64_Data\Managed"]))
        {
        }

        using var patchStream = new MemoryStream(fs.File.ReadAllBytes(PatchPath));
        using var archive = new ZipArchive(patchStream, ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        JsonElement operation = manifest.RootElement.GetProperty("operations").EnumerateArray().Single();

        Assert.Multiple(() =>
        {
            Assert.That(operation.GetProperty("fileName").GetString(), Is.EqualTo(ShapesPath));
            Assert.That(operation.GetProperty("action").GetInt32(), Is.EqualTo((int)PatchOperation.PatchAction.Remove));
            Assert.That(archive.GetEntry(ShapesPath), Is.Null, "A removed DLL must not be included as an add or patch payload.");
        });
    }
}
