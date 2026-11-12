using System.Text.Json.Nodes;
using System.Text.Json;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Services;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.Services.Install;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    private static JsonObject Decode(byte[] bytes) => JsonNode.Parse(Convert.FromBase64String(
        JsonNode.Parse(bytes)!["Payload"]!.GetValue<string>()))!.AsObject();

    private static string LastWork(Fixture fixture)
    {
        JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
        Guid id = head["LastTransaction"]!.GetValue<Guid>();
        return fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}", $"{id:N}-work");
    }

    private static async Task<Fixture> BeforeRetirement()
    {
        Fixture fixture = Create();
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "retirement-authority:open" && !after)
                throw new IOException("Published generation retained before retirement.");
        })));
        Assert.That(fixture.Service().Reconcile(fixture.Entry.Id)!.Release, Is.EqualTo(fixture.Target2.Identity));
        return fixture;
    }

    /// <summary>
    /// Completed installation stages release duplicate bytes while exact stock and user bytes survive later operations.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StorageLifecycle_CompletedPayloadsRetireAndFutureOperationsUseRetainedAuthority()
    {
        Fixture fixture = Create();
        Generation(fixture, await Apply(fixture));
        string work = LastWork(fixture);
        string source = work[..^5] + "-source";
        Assert.That(fixture.Fs.Directory.Exists(source), Is.False, "A committed source copy is disposable.");
        Assert.That(fixture.Fs.Directory.Exists(fixture.Fs.Path.Combine(work, "undo")), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(work, "plan.json")), Is.True);
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(work, "journal")), Is.True);
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(work, "payload-retirement.complete")), Is.True);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
        Generation(fixture, await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None));
        Preserved(fixture, archive);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT));
    }

    /// <summary>
    /// Source staging omits unchanged and explicitly retained bytes before engine mutation.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StorageLifecycle_SourceStageContainsOnlyChangedInputsAndDerivedBoot()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        bool observed = false;
        var service = fixture.Service((name, after) =>
        {
            if (name != "engine-apply" || after)
                return;
            JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
            Guid id = head["Pending"]!.GetValue<Guid>();
            string source = fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}", $"{id:N}-source");
            Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, source, RETAINED)), Is.False);
            Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, source, BUNDLE)), Is.False);
            Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, source, BOOT)), Is.True);
            observed = true;
        });
        await Apply(fixture, service, initial: false);
        Assert.That(observed, Is.True);
    }

    /// <summary>
    /// An interruption before the engine ever starts still permits verified source-stage retirement after reconciliation.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StorageLifecycle_UnstartedEngineRetiresOnlyItsProvenSourceStage()
    {
        Fixture fixture = Create();
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "engine-apply" && !after)
                throw new IOException("Engine never invoked.");
        })));
        fixture.Service().Reconcile(fixture.Entry.Id);
        await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None);
        string work = LastWork(fixture);
        Assert.That(fixture.Fs.Directory.Exists(work[..^5] + "-source"), Is.False);
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(work, "journal")), Is.False,
            "Retirement must not invent an engine journal for an operation that never began.");
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(work, "payload-retirement.complete")), Is.True);
        Generation(fixture, await Apply(fixture));
    }

    /// <summary>
    /// Interrupted retirement metadata for absent engine storage resumes under its independently saved absence witness.
    /// </summary>
    /// <param name="boundary">The interrupted retirement boundary.</param>
    /// <param name="after">True for interruption after completion.</param>
    /// <returns>The verification task.</returns>
    [TestCase("retirement-unstarted:create", false)]
    [TestCase("retirement-unstarted:create", true)]
    [TestCase("retirement-unstarted:plan:open", false)]
    [TestCase("retirement-unstarted:plan:write", true)]
    [TestCase("retirement-unstarted:plan:flush", true)]
    public async Task StorageLifecycle_AbsentEngineStorageRetirementRestarts(string boundary, bool after)
    {
        Fixture fixture = Create();
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, completed) =>
        {
            if (name == "engine-apply" && !completed)
                throw new IOException("Engine never invoked.");
        })));
        Assert.Throws<IOException>(() => fixture.Service(engineFault: (name, completed) =>
        {
            if (name == boundary && completed == after)
                throw new IOException("Independent absent-storage retirement interrupted.");
        }).Reconcile(fixture.Entry.Id));
        Assert.That(await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Is.True);
        Generation(fixture, await Apply(fixture));
    }

    /// <summary>
    /// A legacy retain-all head changes policy durably before its terminal payloads can be retired.
    /// </summary>
    /// <param name="after">True for interruption after policy publication.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false)]
    [TestCase(true)]
    public async Task StorageLifecycle_LegacyPolicyTransitionRestartsBeforeDeleting(bool after)
    {
        Fixture fixture = await BeforeRetirement();
        JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
        head["RetentionPolicy"] = "retain-all-v1";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(head);
        JsonNode envelope = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(fixture.Head))!;
        envelope["Payload"] = Convert.ToBase64String(payload);
        envelope["Sha256"] = Digest(payload);
        fixture.Fs.File.WriteAllBytes(fixture.Head, JsonSerializer.SerializeToUtf8Bytes(envelope));
        string source = LastWork(fixture)[..^5] + "-source";
        await Assert.ThrowsAsync<IOException>(async () => await fixture.Service((name, completed) =>
        {
            if (name == "catalog-retention:publish" && completed == after)
                throw new IOException("Policy transition interrupted.");
        }).RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, source, BUNDLE)), Is.True);
        Assert.That(await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Is.True);
        Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
    }

    /// <summary>
    /// Torn retirement publications are completed only from exact prefixes of independently rebuilt intent.
    /// </summary>
    /// <param name="completion">True for the completion record, false for the unpublished intent candidate.</param>
    /// <param name="length">The retained prefix length.</param>
    /// <returns>The verification task.</returns>
    [TestCase(false, 0)]
    [TestCase(false, 17)]
    [TestCase(true, 0)]
    [TestCase(true, 17)]
    public async Task StorageLifecycle_TornRetirementRecordsResumeExactPrefixes(bool completion, int length)
    {
        Fixture fixture = Create();
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service(engineFault: (name, after) =>
        {
            if (completion ? name == "retirement-complete:write" && after : name == "retirement-intent:publish" && !after)
                throw new IOException("Retirement record publication interrupted.");
        })));
        string record = fixture.Fs.Path.Combine(LastWork(fixture), completion ? "payload-retirement.complete" : "payload-retirement.pending");
        byte[] original = fixture.Fs.File.ReadAllBytes(record);
        fixture.Fs.File.WriteAllBytes(record, original[..length]);
        Assert.That(await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Is.True);
        Assert.That(fixture.Fs.Directory.Exists(LastWork(fixture)[..^5] + "-source"), Is.False);
    }

    /// <summary>
    /// Cancellation after one retired file preserves the committed result and finishes on a fresh retry.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StorageLifecycle_CancellationAfterDeletionKeepsCommittedAuthority()
    {
        Fixture fixture = Create();
        using var cancellation = new CancellationTokenSource();
        var service = fixture.Service(engineFault: (name, after) =>
        {
            if (name == "retirement-delete-file" && after)
                cancellation.Cancel();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Apply(fixture, service, ct: cancellation.Token));
        Assert.That(fixture.Service().Reconcile(fixture.Entry.Id)!.Release, Is.EqualTo(fixture.Target2.Identity));
        Assert.That(await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Is.True);
        Generation(fixture, await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None));
    }

    private static IEnumerable<TestCaseData> RetirementBoundaries()
    {
        Fixture fixture = Create();
        var boundaries = new List<(string Name, bool After)>();
        Task.Run(() => Apply(fixture, fixture.Service((name, after) => boundaries.Add((name, after)),
            (name, after) => boundaries.Add(("engine:" + name, after))))).GetAwaiter().GetResult();
        var distinct = new HashSet<(string, bool)>();
        for (int index = 0; index < boundaries.Count; index++)
        {
            (string name, bool after) = boundaries[index];
            if (!name.Contains("retirement", StringComparison.Ordinal) || !distinct.Add((name, after)))
                continue;
            yield return new TestCaseData(index, name, after).SetName($"StorageLifecycle_Restart_{index}_{name}_{after}");
        }
    }

    /// <summary>
    /// Process loss at every distinct retirement publication or deletion edge preserves the head and retries idempotently.
    /// </summary>
    /// <param name="index">The combined operation-boundary index.</param>
    /// <param name="name">The interrupted boundary name.</param>
    /// <param name="after">True for interruption after the operation.</param>
    /// <returns>The verification task.</returns>
    [TestCaseSource(nameof(RetirementBoundaries))]
    public async Task StorageLifecycle_RestartCompletesWithoutResumingAnOldGeneration(int index, string name, bool after)
    {
        Fixture fixture = Create();
        int count = -1;
        void Fail(string _, bool __)
        {
            if (++count == index)
                throw new IOException($"Retirement interruption at {name} {after}.");
        }
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture,
            fixture.Service(Fail, (operation, completed) => Fail("engine:" + operation, completed))));
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        Generation(fixture, fixture.Service().Reconcile(fixture.Entry.Id)!);
        Assert.That(await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Is.True);
        Assert.That(await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Is.True);
        Assert.That(fixture.Fs.Directory.Exists(LastWork(fixture)[..^5] + "-source"), Is.False);
        Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
        Preserved(fixture, archive);
    }

    /// <summary>
    /// Missing, foreign or changed stage entries and unhealthy backups prevent every payload deletion.
    /// </summary>
    /// <param name="conflict">The independently introduced conflict.</param>
    /// <returns>The verification task.</returns>
    [TestCase("unknown-source")]
    [TestCase("unknown-work")]
    [TestCase("changed-source")]
    [TestCase("missing-source")]
    [TestCase("changed-undo")]
    [TestCase("changed-archive")]
    [TestCase("missing-archive")]
    [TestCase("changed-head")]
    [TestCase("changed-plan")]
    [TestCase("changed-journal")]
    public async Task StorageLifecycle_ConflictsRetainAllPayloads(string conflict)
    {
        Fixture fixture = await BeforeRetirement();
        string work = LastWork(fixture);
        string source = work[..^5] + "-source";
        string undo = fixture.Fs.Directory.EnumerateFiles(fixture.Fs.Path.Combine(work, "undo")).First();
        switch (conflict)
        {
            case "unknown-source": Put(fixture.Fs, source, "unknown.txt", USER_BYTES); break;
            case "unknown-work": Put(fixture.Fs, work, "undo/foreign.txt", USER_BYTES); break;
            case "changed-source": Put(fixture.Fs, source, BUNDLE, "changed source"); break;
            case "missing-source":
                string missing = At(fixture.Fs, source, BUNDLE);
                Assert.That(fixture.Fs.File.Exists(missing), Is.True);
                fixture.Fs.File.Delete(missing);
                break;
            case "changed-undo": fixture.Fs.File.WriteAllText(undo, "changed undo"); break;
            case "changed-archive": fixture.Fs.File.WriteAllText(fixture.Archive, "changed archive"); break;
            case "missing-archive": fixture.Fs.File.Delete(fixture.Archive); break;
            case "changed-head": fixture.Fs.File.WriteAllText(fixture.Head, "changed head"); break;
            case "changed-plan": fixture.Fs.File.WriteAllText(fixture.Fs.Path.Combine(work, "plan.json"), "changed plan"); break;
            case "changed-journal": fixture.Fs.File.WriteAllText(fixture.Fs.Path.Combine(work, "journal"), "changed journal"); break;
        }
        Dictionary<string, string> payload = fixture.Fs.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Concat(fixture.Fs.Directory.EnumerateFiles(fixture.Fs.Path.Combine(work, "undo")))
            .ToDictionary(path => path, path => Digest(fixture.Fs.File.ReadAllBytes(path)));
        Assert.That(async () => await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None),
            Throws.Exception);
        foreach ((string path, string hash) in payload)
        {
            Assert.That(Digest(fixture.Fs.File.ReadAllBytes(path)), Is.EqualTo(hash), path);
        }
        Preserved(fixture);
    }

    /// <summary>
    /// Unexplained fixed-name metadata is rejected before any otherwise disposable file can be deleted.
    /// </summary>
    /// <param name="conflict">The unexplained retirement or recovery entry.</param>
    /// <returns>The verification task.</returns>
    [TestCase("empty-recovery")]
    [TestCase("foreign-retirement-candidate")]
    public async Task StorageLifecycle_UnrecognizedRetirementMetadataPreventsDeletion(string conflict)
    {
        Fixture fixture;
        if (conflict == "empty-recovery")
        {
            fixture = await BeforeRetirement();
        }
        else
        {
            fixture = Create();
            await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service(engineFault: (name, after) =>
            {
                if (name == "retirement-delete-file" && !after)
                    throw new IOException("Inventory published before the first deletion.");
            })));
        }
        string work = LastWork(fixture);
        Put(fixture.Fs, work, conflict == "empty-recovery" ? "recovery" : "payload-retirement.pending",
            conflict == "empty-recovery" ? "" : USER_BYTES);
        Assert.That(async () => await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Throws.Exception);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, work[..^5] + "-source", BUNDLE)), Is.True,
            "Foreign metadata must be rejected before deleting even exact disposable payloads.");
    }

    /// <summary>
    /// Active authority, coordinator exclusion and a running intended game prevent retirement.
    /// </summary>
    /// <param name="conflict">The active owner or transaction signal.</param>
    /// <returns>The verification task.</returns>
    [TestCase("pending")]
    [TestCase("active")]
    [TestCase("lease")]
    [TestCase("game")]
    public async Task StorageLifecycle_ActiveOwnersBlockRetirement(string conflict)
    {
        Fixture fixture = await BeforeRetirement();
        string source = LastWork(fixture)[..^5] + "-source";
        using IDisposable? lease = conflict == "lease" ? fixture.Service().AcquireLaunchLease(fixture.Install) : null;
        if (conflict == "game")
            fixture.GameProcesses.Setup(service => service.IsRunning(fixture.Install)).Returns(true);
        if (conflict == "active")
            Put(fixture.Fs, fixture.Install, ".redux-transaction.active", "independent active marker");
        if (conflict == "pending")
        {
            JsonObject head = Decode(fixture.Fs.File.ReadAllBytes(fixture.Head));
            head["Pending"] = Guid.NewGuid();
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(head);
            JsonNode envelope = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(fixture.Head))!;
            envelope["Payload"] = Convert.ToBase64String(payload);
            envelope["Sha256"] = Digest(payload);
            fixture.Fs.File.WriteAllBytes(fixture.Head, JsonSerializer.SerializeToUtf8Bytes(envelope));
        }
        Assert.That(async () => await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Throws.Exception);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, source, BUNDLE)), Is.True);
    }

    /// <summary>
    /// A completed receipt never reacquires recreated source bytes, even when they match the old contract.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StorageLifecycle_CompletedReceiptRejectsRecreatedPayload()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        string source = LastWork(fixture)[..^5] + "-source";
        Put(fixture.Fs, source, BUNDLE, "full source streaming generation two");
        Assert.That(async () => await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Throws.Exception);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, source, BUNDLE)), Is.EqualTo("full source streaming generation two"));
    }

    /// <summary>
    /// A retired generation remains repairable after later user damage and keeps the original archive.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StorageLifecycle_RepairAfterRetirementUsesNewObservedUndo()
    {
        Fixture fixture = Create();
        await Apply(fixture);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        Put(fixture.Fs, fixture.Install, BUNDLE, "new damaged owned bytes");
        fixture.Fs.File.Delete(At(fixture.Fs, fixture.Install, "added.bin"));
        Generation(fixture, await Apply(fixture, initial: false, repair: true));
        Preserved(fixture, archive);
    }

    /// <summary>
    /// Real Windows stages retire, restart and restore exact original stock using small independent inputs.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test, Platform("Win")]
    public async Task StorageLifecycle_NativeWindowsRetirementAndUninstall()
    {
        Fixture fixture = NativeFixture();
        bool interrupted = false;
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service(engineFault: (name, after) =>
        {
            if (!interrupted && name == "retirement-delete-file" && after)
            {
                interrupted = true;
                throw new IOException("Native retirement interruption after one exact file deletion.");
            }
        })));
        Assert.That(interrupted, Is.True);
        Generation(fixture, fixture.Service().Reconcile(fixture.Entry.Id)!);
        byte[] archive = fixture.Fs.File.ReadAllBytes(fixture.Archive);
        await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None);
        Generation(fixture, await Apply(fixture, target: fixture.Target3, initial: false));
        Generation(fixture, await fixture.Service().UninstallAsync(fixture.Entry.Id, _ => { }, CancellationToken.None));
        await new StockArchive(fixture.Fs, new ZipFileService(fixture.Fs)).VerifyAsync(fixture.Archive, fixture.Stock.Stock);
        Preserved(fixture, archive);
        Assert.That(fixture.Fs.File.ReadAllText(At(fixture.Fs, fixture.Install, BOOT)), Is.EqualTo(STOCK_BOOT));
        string storage = fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        Assert.That(fixture.Fs.Directory.EnumerateDirectories(storage).Any(path => path.EndsWith("-source", StringComparison.Ordinal)), Is.False);
        string report = fixture.Fs.Path.Combine(fixture.Root, "storage-lifecycle-result.json");
        long retiredPayloadBytes = 0;
        foreach (string intentPath in fixture.Fs.Directory.EnumerateFiles(storage, "payload-retirement.json", SearchOption.AllDirectories))
        {
            JsonNode intent = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(intentPath))!;
            retiredPayloadBytes += StockBaselineManifest.FromJson(Convert.FromBase64String(intent["Source"]!.GetValue<string>())).TotalBytes;
            retiredPayloadBytes += StockBaselineManifest.FromJson(Convert.FromBase64String(intent["Work"]!.GetValue<string>()))
                .Files.Where(file => file.Path.Contains('/')).Sum(file => file.Length);
        }
        fixture.Fs.File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new
        {
            fixture.Root, installation = fixture.Entry.Id, archiveSha256 = Digest(archive),
            archiveLength = archive.LongLength, sourceStages = 0, retiredPayloadBytes,
            completedRetirements = fixture.Fs.Directory.EnumerateFiles(storage, "payload-retirement.complete", SearchOption.AllDirectories).Count(),
            interrupted, exactStockRestored = true, unknownFilesPreserved = true
        }));
        TestContext.AddTestAttachment(report, "Small native Windows retirement and exact uninstall evidence.");
    }

    /// <summary>
    /// Retained sibling metadata does not consume the next transaction's protected-root budget.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StorageLifecycle_RetainedSiblingMetadataDoesNotExhaustOperations()
    {
        Fixture fixture = Create();
        string storage = fixture.Fs.Path.Combine(fixture.Root, $".redux-fullsource-{fixture.Entry.Id:N}");
        for (int index = 0; index < 1030; index++)
        {
            string sibling = fixture.Fs.Path.Combine(storage, $"retained-{index:D4}");
            Put(fixture.Fs, sibling, "unknown-metadata.txt", USER_BYTES);
        }
        Generation(fixture, await Apply(fixture));
        Assert.That(fixture.Fs.Directory.EnumerateDirectories(storage).Count(), Is.EqualTo(1031));
        Assert.That(fixture.Fs.File.ReadAllText(fixture.Fs.Path.Combine(storage, "retained-0000", "unknown-metadata.txt")), Is.EqualTo(USER_BYTES));
    }

    /// <summary>
    /// Newly configured roots inside a former stage revoke deletion eligibility.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task StorageLifecycle_NewConfiguredInstallationInsideStageIsPreserved()
    {
        Fixture fixture = await BeforeRetirement();
        string source = LastWork(fixture)[..^5] + "-source";
        fixture.Configuration.Config.Ksp2Installs.Add(new Ksp2InstallEntry
        {
            Name = "New configured root", ExePath = fixture.Fs.Path.Combine(source, Ksp2Install.KSP2_EXE_NAME)
        });
        Assert.That(async () => await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Throws.Exception);
        Assert.That(fixture.Fs.File.Exists(fixture.Fs.Path.Combine(source, Ksp2Install.KSP2_EXE_NAME)), Is.True);
    }

    /// <summary>
    /// Native path validation rejects stage links and named streams before any payload deletion.
    /// </summary>
    /// <param name="conflict">The native unowned metadata kind.</param>
    /// <returns>The verification task.</returns>
    [TestCase("link")]
    [TestCase("directory-stream")]
    [TestCase("file-stream")]
    [Platform("Win")]
    public async Task StorageLifecycle_NativeWindowsRejectsUnownedMetadata(string conflict)
    {
        Fixture fixture = NativeFixture();
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "retirement-authority:open" && !after)
                throw new IOException("Native generation published before retirement.");
        })));
        string source = LastWork(fixture)[..^5] + "-source";
        string empty = fixture.Fs.Path.Combine(source, "empty");
        string outside = fixture.Fs.Path.Combine(fixture.Root, "outside-retirement");
        Put(fixture.Fs, outside, "unique.txt", USER_BYTES);
        string? stream = null;
        if (conflict == "link")
        {
            fixture.Fs.Directory.Delete(empty, false);
            fixture.Fs.Directory.CreateSymbolicLink(empty, outside);
        }
        else
        {
            stream = (conflict == "directory-stream" ? empty : At(fixture.Fs, source, BUNDLE)) + ":unowned-metadata";
            fixture.Fs.File.WriteAllText(stream, USER_BYTES);
        }
        Assert.That(async () => await fixture.Service().RetireCompletedPayloadsAsync(fixture.Entry.Id, CancellationToken.None), Throws.Exception);
        Assert.That(fixture.Fs.File.Exists(At(fixture.Fs, source, BUNDLE)), Is.True);
        Assert.That(fixture.Fs.File.ReadAllText(fixture.Fs.Path.Combine(outside, "unique.txt")), Is.EqualTo(USER_BYTES));
        if (stream is not null)
            Assert.That(fixture.Fs.File.ReadAllText(stream), Is.EqualTo(USER_BYTES));
    }
}
