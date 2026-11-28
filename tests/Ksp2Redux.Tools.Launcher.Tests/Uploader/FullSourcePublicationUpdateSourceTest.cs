using System.Reflection;
using System.Security.Cryptography;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Launcher.Services.Install;

namespace Ksp2Redux.Tools.Launcher.Tests.Uploader;

/// <summary>
/// A prior release contract cached by an older publication, which lists boot.config, still serves as an update source,
/// and the pinned prepatch contract is published with the bytes and digest it is pinned by.
/// </summary>
public class FullSourcePublicationUpdateSourceTest
{
    private static readonly string BOOT_SHA = new('C', 64);

    private static StockBaselineManifest Contract() => new("prior", "cached update source", [
        new StockBaselineFile("KSP2_x64.exe", 1, new string('A', 64)),
        new StockBaselineFile("KSP2_x64_Data/StreamingAssets/aa/catalog.json", 2, new string('B', 64))]);

    private static (StockBaselineManifest Contract, byte[] Bytes) Read(byte[] bytes)
    {
        MethodInfo method = typeof(Ksp2Redux.Tools.Uploader.PatchUploadEntry).Assembly
            .GetType("Ksp2Redux.Tools.Uploader.FullSourcePublication")!
            .GetMethod("ReadUpdateSource", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return ((StockBaselineManifest, byte[]))method.Invoke(null, [bytes])!;
        }
        catch (TargetInvocationException failure)
        {
            throw failure.InnerException!;
        }
    }

    /// <summary>
    /// A contract in the older form is used in the current form, whose digest is the one installations carry.
    /// </summary>
    [Test]
    public void ContractListingBootConfig_IsPublishedWithoutThatEntry()
    {
        StockBaselineManifest contract = Contract();
        byte[] older = contract.ToJsonWithBootConfig(3, BOOT_SHA);
        Assert.That(older, Is.Not.EqualTo(contract.ToJson()));

        var (read, bytes) = Read(older);

        Assert.That(bytes, Is.EqualTo(contract.ToJson()));
        Assert.That(Convert.ToHexString(SHA256.HashData(bytes)), Is.EqualTo(Convert.ToHexString(SHA256.HashData(contract.ToJson()))));
        Assert.That(read.Files.Select(file => file.Path), Does.Not.Contain(BootConfigFile.RELATIVE_PATH));
        Assert.That(Read(contract.ToJson()).Bytes, Is.EqualTo(contract.ToJson()));
    }

    /// <summary>
    /// Bytes that are neither form of the contract still fail.
    /// </summary>
    [Test]
    public void NoncanonicalContract_StillFails()
    {
        byte[] spaced = [.. " "u8, .. Contract().ToJson()];

        Assert.Throws<InvalidDataException>(() => Read(spaced));
    }

    /// <summary>
    /// The pinned prepatch contract lists boot.config. It is published byte for byte, so the digest the launcher derives
    /// the names of its original-stock contracts, caches and backups from stays the pinned one, while the launcher reads
    /// the contract without that entry.
    /// </summary>
    [Test]
    public void PinnedPrepatchListingBootConfig_IsPublishedUnchangedAndReadWithoutThatEntry()
    {
        StockBaselineManifest contract = Contract();
        byte[] pinned = contract.ToJsonWithBootConfig(3, BOOT_SHA);
        string pinnedSha = Convert.ToHexString(SHA256.HashData(pinned));
        MethodInfo method = typeof(Ksp2Redux.Tools.Uploader.PatchUploadEntry).Assembly
            .GetType("Ksp2Redux.Tools.Uploader.FullSourcePublication")!
            .GetMethod("ReadPinnedPrepatch", BindingFlags.NonPublic | BindingFlags.Static)!;

        var (published, read) = ((byte[], StockBaselineManifest))method.Invoke(null, [pinned, pinnedSha])!;

        Assert.That(published, Is.EqualTo(pinned));
        Assert.That(read.Files.Select(file => file.Path), Does.Not.Contain(BootConfigFile.RELATIVE_PATH));
        var wrong = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [pinned, new string('0', 64)]))!;
        Assert.That(wrong.InnerException, Is.InstanceOf<InvalidDataException>());

        var descriptor = new FullSourceReleaseDescriptor("release", "beta", "1.2.3.4.5", contract.ToJson(),
            Convert.ToHexString(SHA256.HashData(contract.ToJson())), "https://fixture.test/release.json", "https://fixture-backup.test/release.json",
            [new FullSourceBaselineDefinition(contract.ToJson(), [])], 1)
        {
            PrepatchManifest = published, PrepatchManifestSha256 = pinnedSha,
            PrepatchResources = new Dictionary<string, string> { ["Steam"] = BOOT_SHA, ["Epic"] = BOOT_SHA, ["Portable"] = BOOT_SHA }
        };
        Assert.That(ReleaseManifestValidator.GetPrepatchBaseline(descriptor)!.ToJson(), Is.EqualTo(contract.ToJson()));
        MethodInfo identity = typeof(FullSourcePreparationService).GetMethod("OriginalBaselineIdentity", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (string distribution in new[] { "Steam", "Epic", "Portable" })
            Assert.That(identity.Invoke(null, [descriptor, distribution]),
                Is.EqualTo($"redux-original-windows-{distribution.ToLowerInvariant()}-{pinnedSha.ToLowerInvariant()}"));
    }
}
