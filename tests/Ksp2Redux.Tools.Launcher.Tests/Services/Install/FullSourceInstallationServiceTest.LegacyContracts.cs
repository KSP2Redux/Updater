using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Common.Transactions;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// Installations, journals and backups written before boot.config left every contract keep working.
/// </summary>
public partial class FullSourceInstallationServiceTest
{
    private const string BOOT_CONFIG_FILE_BYTES = "legacy boot.config as an earlier build recorded it";

    // The contract as an earlier build wrote it: the same files plus one boot.config entry.
    private static byte[] LegacyContract(byte[] canonical, byte[]? bootConfig = null)
    {
        bootConfig ??= Encoding.UTF8.GetBytes(BOOT_CONFIG_FILE_BYTES);
        var node = JsonNode.Parse(canonical)!.AsObject();
        var files = node["files"]!.AsArray();
        var list = files.Select(file => file!.DeepClone()).ToList();
        list.Add(new JsonObject
        {
            ["path"] = BOOT, ["length"] = bootConfig.Length, ["sha256"] = Digest(bootConfig)
        });
        list = list.OrderBy(file => (string)file!["path"]!, StringComparer.Ordinal).ToList();
        files.Clear();
        foreach (var file in list)
            files.Add(file);
        byte[] legacy = Encoding.UTF8.GetBytes(node.ToJsonString());
        Assert.That(StockBaselineManifest.FromJson(legacy).IsLegacyForm(legacy), Is.True);
        Assert.That(StockBaselineManifest.FromJson(legacy).ToJson(), Is.EqualTo(canonical));
        return legacy;
    }

    // Plain JsonNode text escapes the plus sign of base64, which the coordinator's own serializer does not.
    private static byte[] Json(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString().Replace("\\u002B", "+"));

    private static void Rewrite(Fixture fixture, string path, Action<JsonObject> change)
    {
        JsonNode envelope = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(path))!;
        JsonObject payload = JsonNode.Parse(Convert.FromBase64String(envelope["Payload"]!.GetValue<string>()))!.AsObject();
        change(payload);
        byte[] changed = Json(payload);
        envelope["Payload"] = Convert.ToBase64String(changed);
        envelope["Sha256"] = Digest(changed);
        fixture.Fs.File.WriteAllBytes(path, Json(envelope));
    }

    private static string OperationFile(Fixture fixture, JsonObject head) => fixture.Fs.Path.Combine(
        fixture.Storage, "full-source-v1", fixture.Entry.Id.ToString("N"), $"{head["LastTransaction"]!.GetValue<Guid>():N}.json");

    private static void LegacyBytes(JsonObject node, string field) =>
        node[field] = Convert.ToBase64String(LegacyContract(Convert.FromBase64String(node[field]!.GetValue<string>())));

    /// <summary>
    /// Rewrite the head and the last journal the way an earlier build stored them: boot.config in Stock, Ownership and
    /// the operation contracts, and the release named by the digest of the contract that lists it.
    /// </summary>
    /// <param name="fixture">The installed fixture.</param>
    /// <param name="releaseDigest">The release identity digest to store, or null to digest the legacy release contract.</param>
    private static void WriteLegacyRecords(Fixture fixture, string? releaseDigest = null)
    {
        JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
        string operation = OperationFile(fixture, head);
        Rewrite(fixture, fixture.Head, catalog =>
        {
            LegacyBytes(catalog, "Stock");
            LegacyBytes(catalog, "Ownership");
            // Earlier builds did not record the release's boot.config settings.
            catalog.Remove("ReleaseBootConfig");
            if (catalog["Release"] is JsonObject identity)
                identity["ManifestSha256"] = releaseDigest ?? Digest(LegacyContract(fixture.Target2.Manifest.ToJson()));
        });
        Rewrite(fixture, operation, journal =>
        {
            LegacyBytes(journal, "Current");
            LegacyBytes(journal, "Target");
            LegacyBytes(journal, "ReleaseManifest");
            journal.Remove("BootConfigSettings");
            if (journal["Release"] is JsonObject identity)
                identity["ManifestSha256"] = releaseDigest ?? Digest(LegacyContract(fixture.Target2.Manifest.ToJson()));
        });
    }

    private static void ExpectCatalogMigrated(Fixture fixture)
    {
        JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
        Assert.That(Convert.FromBase64String(head["Stock"]!.GetValue<string>()), Is.EqualTo(fixture.Stock.Stock.ToJson()));
        Assert.That(Convert.FromBase64String(head["Ownership"]!.GetValue<string>()),
            Is.EqualTo(StockBaselineManifest.FromJson(Convert.FromBase64String(head["Ownership"]!.GetValue<string>())).ToJson()));
    }

    /// <summary>
    /// A catalog and last journal written before boot.config left the contracts are migrated when read, and the
    /// installation still launches, updates, repairs and uninstalls.
    /// </summary>
    /// <param name="operation">What the installation does after the migration.</param>
    /// <returns>The verification task.</returns>
    [TestCase("launch")]
    [TestCase("update")]
    [TestCase("repair")]
    [TestCase("uninstall")]
    public async Task LegacyCatalogAndJournal_AreMigratedAndEveryEntryPointKeepsWorking(string operation)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        WriteLegacyRecords(fixture);
        byte[] legacyHead = fixture.Fs.File.ReadAllBytes(fixture.Head);

        Assert.That(fixture.Service().HasCatalog(fixture.Install), Is.True);
        switch (operation)
        {
            case "launch":
                new InstallationStateService(fixture.Fs, new ZipFileService(fixture.Fs), fixture.Service()).EnsureLaunchAllowed(fixture.Install);
                fixture.Service().AcquireLaunchLease(fixture.Install)?.Dispose();
                break;
            case "update":
                Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
                break;
            case "repair":
                Put(fixture.Fs, fixture.Install, BUNDLE, "damage the repair replaces");
                Generation(fixture, await Apply(fixture, initial: false, repair: true));
                break;
            default:
                Generation(fixture, await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None));
                Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT));
                Preserved(fixture, archive);
                break;
        }

        Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Head), Is.Not.EqualTo(legacyHead));
        ExpectCatalogMigrated(fixture);
    }

    /// <summary>
    /// The migration is written once. A later read changes nothing, and the next update retires its own payloads.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LegacyCatalog_IsPersistedCanonicallyOnTheFirstRead()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        WriteLegacyRecords(fixture);

        _ = fixture.Service().GetPublishedIdentity(fixture.Entry.Id);
        byte[] migrated = fixture.Fs.File.ReadAllBytes(fixture.Head);
        ExpectCatalogMigrated(fixture);
        _ = fixture.Service().GetPublishedIdentity(fixture.Entry.Id);
        fixture.Service().AcquireLaunchLease(fixture.Install)?.Dispose();

        Assert.That(fixture.Fs.File.ReadAllBytes(fixture.Head), Is.EqualTo(migrated));
        Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(LastWork(fixture), "payload-retirement.complete")), Is.True);
    }

    /// <summary>
    /// When the ownership contract is the release contract, the release identity follows the normalized digest, so
    /// the installation still matches the update sources a new publication names.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LegacyCatalog_ReleaseIdentityFollowsTheNormalizedContract()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
        byte[] ownership = Convert.FromBase64String(head["Ownership"]!.GetValue<string>());
        WriteLegacyRecords(fixture, Digest(LegacyContract(ownership)));

        FullSourceReleaseIdentity identity = fixture.Service().GetPublishedIdentity(fixture.Entry.Id)!;

        Assert.That(identity.ManifestSha256, Is.EqualTo(Digest(ownership)));
        Assert.That(identity.PublishedManifestSha256, Is.EqualTo(Digest(LegacyContract(ownership))));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LegacyCatalog_MigratesTheCommittedReleaseAfterARollback(bool committedOperationInitiallyMissing)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        string committedOperation = OperationFile(fixture, Decode(fixture.Fs.File.ReadAllBytes(fixture.Head)));
        await Assert.ThrowsAsync<IOException>(() => Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "engine-apply" && !after)
                throw new IOException("Interrupted update");
        }), fixture.Target3, initial: false));
        Assert.That(fixture.Service().Reconcile(fixture.Entry.Id)!.TransactionResult, Is.EqualTo(InstallTransactionResult.RolledBack));
        string legacyDigest = Digest(LegacyContract(fixture.Target2.Manifest.ToJson()));
        Rewrite(fixture, fixture.Head, catalog =>
        {
            LegacyBytes(catalog, "Stock");
            LegacyBytes(catalog, "Ownership");
            catalog.Remove("ReleaseBootConfig");
            catalog["Release"]!["ManifestSha256"] = legacyDigest;
        });
        Rewrite(fixture, committedOperation, journal =>
        {
            LegacyBytes(journal, "Current");
            LegacyBytes(journal, "Target");
            LegacyBytes(journal, "ReleaseManifest");
            journal.Remove("BootConfigSettings");
            journal["Release"]!["ManifestSha256"] = legacyDigest;
        });
        if (committedOperationInitiallyMissing)
        {
            byte[] journal = fixture.Fs.File.ReadAllBytes(committedOperation);
            fixture.Fs.File.Delete(committedOperation);
            Assert.That(fixture.Service().GetPublishedIdentity(fixture.Entry.Id)!.ManifestSha256, Is.EqualTo(legacyDigest));
            fixture.Fs.File.WriteAllBytes(committedOperation, journal);
        }
        FullSourceReleaseIdentity expected = fixture.Target2.Identity with { PublishedManifestSha256 = legacyDigest };
        Assert.That(fixture.Service().GetPublishedIdentity(fixture.Entry.Id), Is.EqualTo(expected));
        FullSourceReleaseIdentity reopened = fixture.Service().GetPublishedIdentity(fixture.Entry.Id)!;
        Assert.That(reopened, Is.EqualTo(expected));
        var (feed, version) = await FeedOf(LegacyContract(fixture.Target2.Manifest.ToJson()), expected.ReleaseId);
        var installed = new Ksp2Install(fixture.Fs, Mock.Of<IModuleDefinitionService>(), fixture.Entry.ExePath, reopened);
        Assert.That(installed.IsInstalledVersion(version, feed.GetFullSourcePlan(version, repair: true)), Is.True);
        Assert.That(Decode(fixture.Fs.File.ReadAllBytes(fixture.Head))["ReleaseIdentityMigrationPending"], Is.Null);
    }

    // Add boot.config to a stock backup and give it the older embedded manifest, then pin the new bytes.
    private static void WriteLegacyArchive(Fixture fixture, bool corruptManifest = false, string? extraEntry = null,
        byte[]? archivedBootConfig = null)
    {
        archivedBootConfig ??= Encoding.UTF8.GetBytes(BOOT_CONFIG_FILE_BYTES);
        byte[] bytes = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        using (var stream = new MemoryStream())
        {
            stream.Write(bytes);
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
            {
                ZipArchiveEntry manifest = zip.GetEntry(StockBaselineManifest.MANIFEST_PATH)!;
                byte[] canonical;
                using (Stream input = manifest.Open())
                {
                    using var copy = new MemoryStream();
                    input.CopyTo(copy);
                    canonical = copy.ToArray();
                }
                manifest.Delete();
                byte[] legacy = LegacyContract(canonical, archivedBootConfig);
                if (corruptManifest)
                    legacy = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(legacy).Replace("storefront/build-original", "storefront/other"));
                using (Stream output = zip.CreateEntry(StockBaselineManifest.MANIFEST_PATH, CompressionLevel.NoCompression).Open())
                    output.Write(legacy);
                using (Stream output = zip.CreateEntry(BOOT, CompressionLevel.NoCompression).Open())
                    output.Write(archivedBootConfig);
                if (extraEntry is not null)
                {
                    using Stream output = zip.CreateEntry(extraEntry, CompressionLevel.NoCompression).Open();
                    output.Write("unexpected"u8);
                }
            }
            fixture.Fs.File.WriteAllBytes(fixture.Archive, stream.ToArray());
        }
        byte[] changed = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        Action<JsonObject> pin = payload =>
        {
            payload["Archive"]!["Length"] = changed.LongLength;
            payload["Archive"]!["Sha256"] = Digest(changed);
        };
        string operation = OperationFile(fixture, Decode(fixture.Fs.File.ReadAllBytes(fixture.Head)));
        Rewrite(fixture, operation, pin);
        Rewrite(fixture, fixture.Head, pin);
    }

    /// <summary>
    /// A backup that still holds boot.config verifies with that entry ignored, and restoring it leaves the current file.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LegacyBackup_VerifiesWithoutBootConfigAndUninstallLeavesTheCurrentFile()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        WriteLegacyRecords(fixture);
        WriteLegacyArchive(fixture);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        var stockArchive = new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs));
        await stockArchive.VerifyAsync(fixture.Archive, fixture.Stock.Stock);
        fixture.Fs.File.WriteAllText(At(fixture.Fs, fixture.Install, BOOT), "custom=1\r\n");

        FullSourceInstallationStatus restored = await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None);

        Generation(fixture, restored);
        Assert.That(restored.Generation.ToJson(), Is.EqualTo(fixture.Stock.Stock.ToJson()));
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo("custom=1\r\n"));
        Preserved(fixture, archive);
        string restoredTree = fixture.Fs.Path.Combine(fixture.Root, "restored-legacy");
        await stockArchive.RestoreToNewDirectoryAsync(fixture.Archive, fixture.Stock.Stock, restoredTree);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, restoredTree, BOOT)), Is.False);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, restoredTree, BUNDLE)), Is.EqualTo("original complete streaming asset"));
    }

    /// <summary>
    /// Every other check of an older backup stays strict: its embedded manifest must be the older form of the
    /// expected contract, and no entry besides boot.config may be extra.
    /// </summary>
    /// <param name="corruptManifest">Whether the embedded manifest names another contract.</param>
    /// <param name="extraEntry">An unexpected entry, or null.</param>
    /// <returns>The verification task.</returns>
    [TestCase(true, null)]
    [TestCase(false, "KSP2_x64_Data/other.bin")]
    public async Task LegacyBackup_StaysStrictAboutEverythingButBootConfig(bool corruptManifest, string? extraEntry)
    {
        Fixture fixture = Create();
        await Apply(fixture);
        WriteLegacyArchive(fixture, corruptManifest, extraEntry);
        var stockArchive = new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await stockArchive.VerifyAsync(fixture.Archive, fixture.Stock.Stock));
    }
}
