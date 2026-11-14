using System.Text.Json;
using System.Text.Json.Nodes;
using Ksp2Redux.Tools.Common.Restoration;
using Ksp2Redux.Tools.Common.Transactions;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

public partial class FullSourceInstallationServiceTest
{
    /// <summary>
    /// Existing ready operations without the new storage field reconstruct the original complete-staging plan and recover.
    /// </summary>
    /// <returns>The verification task.</returns>
    [Test]
    public async Task LegacyReadyOperationWithoutChangedStagingFlag_RecoversOriginalPlan()
    {
        Fixture fixture = Create();
        await Assert.ThrowsAsync<IOException>(async () => await Apply(fixture, fixture.Service((name, after) =>
        {
            if (name == "operation-ready:publish" && after)
                throw new IOException("Interrupt before engine invocation.");
        })));
        JsonNode headEnvelope = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(fixture.Head))!;
        JsonNode head = JsonNode.Parse(Convert.FromBase64String(headEnvelope["Payload"]!.GetValue<string>()))!;
        Guid id = head["Pending"]!.GetValue<Guid>();
        string path = fixture.Fs.Path.Combine(fixture.Fs.Path.GetDirectoryName(fixture.Head)!, $"{id:N}.json");
        JsonNode envelope = JsonNode.Parse(fixture.Fs.File.ReadAllBytes(path))!;
        JsonNode operation = JsonNode.Parse(Convert.FromBase64String(envelope["Payload"]!.GetValue<string>()))!;
        var plan = new InstallTransactionPlan(id, fixture.Install, operation["SourceRoot"]!.GetValue<string>(),
            operation["WorkRoot"]!.GetValue<string>(),
            StockBaselineManifest.FromJson(Convert.FromBase64String(operation["Current"]!.GetValue<string>())),
            StockBaselineManifest.FromJson(Convert.FromBase64String(operation["Target"]!.GetValue<string>())),
            fixture.Stock.Stock, operation["Archive"]!.Deserialize<StockBaselineFile>()!, [RETAINED], ["shared"],
            operation["ProtectedRoots"]!.Deserialize<string[]>()!);
        operation.AsObject().Remove("ChangedFileStaging");
        operation.AsObject().Remove("SparseSourceStaging");
        operation.AsObject().Remove("MovedFileUndo");
        operation.AsObject().Remove("OwnedSourceStaging");
        Put(fixture.Fs, operation["SourceRoot"]!.GetValue<string>(), RETAINED, "explicit retained bytes");
        operation["PlanDigest"] = plan.GetDigest();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(operation);
        envelope["Payload"] = Convert.ToBase64String(payload);
        envelope["Sha256"] = Digest(payload);
        fixture.Fs.File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(envelope));
        var oldOperation = new InstallTransactionEngine(fixture.Fs, _ => long.MaxValue, (name, after) =>
        {
            if (name == "install-0" && after)
                throw new IOException("Legacy complete-staging publication interrupted.");
        }, _ => { });
        Assert.Throws<IOException>(() => oldOperation.Apply(plan));
        var recovered = fixture.Service().Reconcile(fixture.Entry.Id)!;
        Assert.That(recovered.TransactionResult, Is.EqualTo(InstallTransactionResult.RolledBack));
        Assert.That(recovered.Release, Is.Null);
        Generation(fixture, recovered);
    }
}
