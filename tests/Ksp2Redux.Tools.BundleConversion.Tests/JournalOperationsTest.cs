using System.Text.Json;

namespace Ksp2Redux.Tools.BundleConversion.Tests;

/// <summary>
/// Revert, verify and the old-bytes guard, driven purely by a journal over plain files.
/// </summary>
public class JournalOperationsTest
{
    private const string FILE = "plain.bundle";
    private const long OFFSET_A = 16;
    private const long OFFSET_B = 40;

    private static readonly byte[] OLD_A = Hex.PPtr(0, 5);
    private static readonly byte[] NEW_A = Hex.PPtr(1, 123456789);
    private static readonly byte[] OLD_B = Hex.PPtr(0, 6);
    private static readonly byte[] NEW_B = Hex.PPtr(1, 987654321);

    [Test]
    public void Verify_EachEditState_IsReportedPerEdit()
    {
        // Arrange
        var (game, _) = Setup(applyA: true, applyB: false);

        // Act
        var result = game.Converter().Verify();

        // Assert
        Assert.That(result.HasJournal, Is.True);
        Assert.That(result.Edits.Select(edit => (edit.Offset, edit.State)), Is.EqualTo(new[]
        {
            (OFFSET_A, EditState.Applied),
            (OFFSET_B, EditState.Stock),
        }));
        Assert.That(result.IsComplete, Is.False);
    }

    [Test]
    public void Verify_FileChangedSize_ReportsFileChanged()
    {
        // Arrange
        var (game, bytes) = Setup(applyA: true, applyB: true);
        game.Write(FILE, [.. bytes, 0]);

        // Act
        var result = game.Converter().Verify();

        // Assert
        Assert.That(result.Edits.Select(edit => edit.State), Is.All.EqualTo(EditState.FileChanged));
    }

    [Test]
    public void Verify_NoJournal_ReportsNotConverted()
    {
        // Act
        var result = new TestGame().Converter().Verify();

        // Assert
        Assert.That(result.HasJournal, Is.False);
        Assert.That(result.IsComplete, Is.False);
    }

    [Test]
    public void Revert_AppliedAndStockEdits_RestoresStockAndIsSafeToRepeat()
    {
        // Arrange
        var (game, _) = Setup(applyA: true, applyB: false);
        var stock = Stock();

        // Act
        var first = game.Converter().Revert();
        var second = game.Converter().Revert();

        // Assert
        Assert.That(first.EditsReverted, Is.EqualTo(1));
        Assert.That(first.EditsAlreadyStock, Is.EqualTo(1));
        Assert.That(first.JournalDeleted, Is.True);
        Assert.That(game.Read(FILE), Is.EqualTo(stock));
        Assert.That(second.HadJournal, Is.False);
        Assert.That(game.Read(FILE), Is.EqualTo(stock));
    }

    [Test]
    public void Revert_ForeignBytes_AreLeftAlone()
    {
        // Arrange: something other than the converter wrote over edit A.
        var (game, bytes) = Setup(applyA: true, applyB: true);
        var foreign = Hex.PPtr(9, 9);
        Buffer.BlockCopy(foreign, 0, bytes, (int)OFFSET_A, foreign.Length);
        game.Write(FILE, bytes);

        // Act
        var result = game.Converter().Revert();

        // Assert
        Assert.That(result.EditsReverted, Is.EqualTo(1));
        Assert.That(result.Unreverted.Select(edit => (edit.Offset, edit.State)), Is.EqualTo(new[] { (OFFSET_A, EditState.Foreign) }));
        var after = game.Read(FILE);
        Assert.That(after.AsSpan((int)OFFSET_A, 12).ToArray(), Is.EqualTo(foreign));
        Assert.That(after.AsSpan((int)OFFSET_B, 12).ToArray(), Is.EqualTo(OLD_B));
    }

    [Test]
    public void Revert_DeletesJournaledCopiesButNothingOutsideTheConversionFolder()
    {
        // Arrange: a copy where the converter puts it, and a tampered entry pointing at the game exe.
        var (game, _) = Setup(applyA: false, applyB: false);
        var copy = game.Layout.Resolve(BundleConversionLayout.RelativeOutput("copied.bundle"));
        game.FileSystem.Directory.CreateDirectory(game.Layout.OutputDirectory);
        game.FileSystem.File.WriteAllBytes(copy, [1, 2, 3]);
        var exe = game.FileSystem.Path.Combine(TestGame.ROOT, "KSP2_x64.exe");
        game.FileSystem.File.WriteAllBytes(exe, [4, 5, 6]);

        var store = new JournalStore(game.FileSystem, game.Layout.JournalPath);
        var journal = store.Read()!;
        journal.Rewritten.Add(new JournalRewrittenFile { File = "copied.bundle", Output = BundleConversionLayout.RelativeOutput("copied.bundle") });
        journal.Rewritten.Add(new JournalRewrittenFile { File = "evil.bundle", Output = "KSP2_x64.exe" });
        store.Write(journal);

        // Act
        var result = game.Converter().Revert();

        // Assert
        Assert.That(result.CopiesDeleted, Is.EqualTo(1));
        Assert.That(game.FileSystem.File.Exists(copy), Is.False);
        Assert.That(game.FileSystem.File.Exists(exe), Is.True);
        Assert.That(result.JournalDeleted, Is.False);
        Assert.That(result.Failures.Single().File, Is.EqualTo("evil.bundle"));
    }

    [Test]
    public void WriteIfMatches_BytesDifferFromTheExpectedOldBytes_LeavesTheFileAlone()
    {
        // Arrange
        var bytes = Stock();
        using MemoryStream stream = new(bytes);

        // Act
        var written = InPlaceEditor.WriteIfMatches(stream, OFFSET_A, OLD_B, NEW_A);

        // Assert
        Assert.That(written, Is.False);
        Assert.That(bytes, Is.EqualTo(Stock()));
    }

    [Test]
    public void WriteIfMatches_ExpectedOldBytes_WritesTheNewOnes()
    {
        // Arrange
        var bytes = Stock();
        using MemoryStream stream = new(bytes);

        // Act
        var written = InPlaceEditor.WriteIfMatches(stream, OFFSET_A, OLD_A, NEW_A);

        // Assert
        Assert.That(written, Is.True);
        Assert.That(bytes.AsSpan((int)OFFSET_A, 12).ToArray(), Is.EqualTo(NEW_A));
    }

    [Test]
    public void JournalStore_Write_UsesTheSchemaTheGameReads()
    {
        // Arrange
        TestGame game = new();
        var store = new JournalStore(game.FileSystem, game.Layout.JournalPath);
        ConversionJournal journal = new()
        {
            ShaderManifestSha256 = "ab",
            InPlace = [new JournalInPlaceFile { File = "x.bundle", Size = 123, Edits = [new JournalEdit { Offset = 7, Old = "00", New = "01" }] }],
            Rewritten = [new JournalRewrittenFile { File = "y.bundle", StockSize = 456, StockSha256 = "cd", Output = "Redux/BundleConversion/StandaloneWindows64/y.bundle", OutputSha256 = "ef" }],
            Skipped = [new JournalSkipped { File = "z.bundle", Shader = "<CAB-1:2>", Reason = "not in manifest" }],
        };

        // Act
        store.Write(journal);

        // Assert
        using var document = JsonDocument.Parse(game.FileSystem.File.ReadAllText(game.Layout.JournalPath));
        var root = document.RootElement;
        Assert.That(Names(root), Is.EqualTo(new[] { "Version", "ShaderManifestSha256", "BundleRoot", "InPlace", "Rewritten", "Skipped" }));
        Assert.That(root.GetProperty("Version").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("BundleRoot").GetString(), Is.EqualTo("KSP2_x64_Data/StreamingAssets/aa/StandaloneWindows64"));
        Assert.That(Names(root.GetProperty("InPlace")[0]), Is.EqualTo(new[] { "File", "Size", "Edits" }));
        Assert.That(Names(root.GetProperty("InPlace")[0].GetProperty("Edits")[0]), Is.EqualTo(new[] { "Offset", "Old", "New" }));
        Assert.That(Names(root.GetProperty("Rewritten")[0]), Is.EqualTo(new[] { "File", "StockSize", "StockSha256", "Output", "OutputSha256" }));
        Assert.That(Names(root.GetProperty("Skipped")[0]), Is.EqualTo(new[] { "File", "Shader", "Reason" }));
        Assert.That(root.GetProperty("Skipped")[0].GetProperty("Shader").GetString(), Is.EqualTo("<CAB-1:2>"));
        Assert.That(game.FileSystem.File.Exists(game.Layout.JournalPath + ".tmp"), Is.False);
    }

    [Test]
    public void JournalStore_Read_UnreadableJournalIsAnError()
    {
        // Arrange
        TestGame game = new();
        game.FileSystem.Directory.CreateDirectory(game.Layout.ConversionDirectory);
        game.FileSystem.File.WriteAllText(game.Layout.JournalPath, "{ not json");

        // Act and assert: treating it as empty would forget edits only it can revert.
        Assert.Throws<InvalidDataException>(() => game.Converter().Revert());
    }

    [TestCase("Reentry/Shockwave", 8261063249168317908L)]
    [TestCase("KSP2/Scenery/Standard (Procedural)", 939470783903551976L)]
    [TestCase("KSP2/Environment/CelestialBody/CelestialBody_Local_Shadow", 6904481296752958924L)]
    public void SyntheticPathId_MatchesTheShaderBundleBuild(string name, long expected)
    {
        // The values come from the Python prototype, which the Unity shader bundle build also matches.
        Assert.That(ShaderBundleIds.SyntheticPathId(name), Is.EqualTo(expected));
    }

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];

    private static byte[] Stock()
    {
        var bytes = new byte[64];
        Buffer.BlockCopy(OLD_A, 0, bytes, (int)OFFSET_A, 12);
        Buffer.BlockCopy(OLD_B, 0, bytes, (int)OFFSET_B, 12);
        return bytes;
    }

    private static (TestGame Game, byte[] Bytes) Setup(bool applyA, bool applyB)
    {
        TestGame game = new();
        var bytes = Stock();
        if (applyA)
        {
            Buffer.BlockCopy(NEW_A, 0, bytes, (int)OFFSET_A, 12);
        }

        if (applyB)
        {
            Buffer.BlockCopy(NEW_B, 0, bytes, (int)OFFSET_B, 12);
        }

        game.Write(FILE, bytes);
        new JournalStore(game.FileSystem, game.Layout.JournalPath).Write(new ConversionJournal
        {
            ShaderManifestSha256 = "00",
            InPlace =
            [
                new JournalInPlaceFile
                {
                    File = FILE,
                    Size = bytes.Length,
                    Edits =
                    [
                        new JournalEdit { Offset = OFFSET_A, Old = Hex.Encode(OLD_A), New = Hex.Encode(NEW_A) },
                        new JournalEdit { Offset = OFFSET_B, Old = Hex.Encode(OLD_B), New = Hex.Encode(NEW_B) },
                    ],
                },
            ],
        });

        return (game, bytes);
    }
}
