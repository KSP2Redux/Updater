using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// The release descriptor carries the player's boot.config settings and still reads older descriptors.
/// </summary>
public class FullSourceReleaseDescriptorTest
{
    private const string LEGACY_TARGET = """
        {"formatVersion":1,"hashAlgorithm":"SHA-256","baselineIdentity":"legacy","provenance":"lists boot.config","files":[
        {"path":"KSP2_x64.exe","length":1,"sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},
        {"path":"KSP2_x64_Data/StreamingAssets/aa/catalog.json","length":2,"sha256":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"},
        {"path":"KSP2_x64_Data/boot.config","length":3,"sha256":"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"}],
        "directories":["KSP2_x64_Data","KSP2_x64_Data/StreamingAssets","KSP2_x64_Data/StreamingAssets/aa"]}
        """;

    private static ReleasePatch Patch(Dictionary<string, string>? settings, string manifestJson = "")
    {
        byte[] target = Encoding.UTF8.GetBytes(manifestJson.Replace("\r", "").Replace("\n", "").Replace("        ", ""));
        if (target.Length == 0)
            target = new StockBaselineManifest("target", "fixture", [
                new StockBaselineFile("KSP2_x64.exe", 1, new string('A', 64)),
                new StockBaselineFile("KSP2_x64_Data/StreamingAssets/aa/catalog.json", 2, new string('B', 64))]).ToJson();
        string digest = Convert.ToHexString(SHA256.HashData(target));
        return new ReleasePatch
        {
            Version = "1.2.3.4.5", Requires = new PatchRequirement(), ChecksumSha256 = digest, Size = 1, ReleasedAt = DateTime.UnixEpoch,
            FullSource = new FullSourceReleaseDescriptor("r", "beta", "1.2.3.4.5", target, digest,
                "https://fixture.test/release.json", "https://fixture.test/release.json",
                [new FullSourceBaselineDefinition(target, [])], 1) { BootConfigSettings = settings }
        };
    }

    /// <summary>
    /// Settings survive serialization, a descriptor without them stays valid, and the retired flag is only read.
    /// </summary>
    [Test]
    public void BootConfigSettings_RoundTripAndRetiredFlagIsOnlyRead()
    {
        var settings = new Dictionary<string, string> { ["gfx-threading-mode"] = "6", ["enable-directstorage"] = "True" };
        FullSourceReleaseDescriptor descriptor = Patch(settings).FullSource!;

        string json = JsonSerializer.Serialize(descriptor);
        Assert.That(json, Does.Not.Contain("AllowsDerivedBootConfig"));
        Assert.That(JsonSerializer.Deserialize<FullSourceReleaseDescriptor>(json)!.BootConfigSettings, Is.EqualTo(settings));
        Assert.That(JsonSerializer.Serialize(Patch(null).FullSource), Does.Not.Contain("BootConfigSettings"));

        string older = json.Replace("\"DownloadBytes\"", "\"AllowsDerivedBootConfig\":false,\"DownloadBytes\"");
        Assert.That(JsonSerializer.Deserialize<FullSourceReleaseDescriptor>(older)!.BootConfigSettings, Is.EqualTo(settings));
        Assert.DoesNotThrow(() => ReleaseManifestValidator.ValidatePatch(Patch(settings)));
    }

    /// <summary>
    /// A release cannot carry the build identity, empty or spaced keys, a key with an equals sign, a null value, or line breaks.
    /// </summary>
    /// <param name="key">The setting name.</param>
    /// <param name="value">The setting value.</param>
    [TestCase("build-guid", "8ea2")]
    [TestCase("", "1")]
    [TestCase("two words", "1")]
    [TestCase("a=b", "1")]
    [TestCase("key", null)]
    [TestCase("key", "line\nbreak")]
    [TestCase("key", "line\rbreak")]
    [TestCase("line\nbreak", "1")]
    [TestCase("line\rbreak", "1")]
    public void InvalidSettings_AreRejected(string key, string? value) =>
        Assert.Throws<InvalidDataException>(() => ReleaseManifestValidator.ValidatePatch(Patch(new() { [key] = value! })));

    /// <summary>
    /// A descriptor published before this rule, whose contract still lists boot.config, still validates.
    /// </summary>
    [Test]
    public void ContractListingBootConfig_StillValidates()
    {
        ReleasePatch patch = Patch(null, LEGACY_TARGET);
        Assert.DoesNotThrow(() => ReleaseManifestValidator.ValidatePatch(patch));
        Assert.That(StockBaselineManifest.FromJson(patch.FullSource!.TargetManifest).Files.Select(file => file.Path),
            Does.Not.Contain(BootConfigFile.RELATIVE_PATH));
    }
}
