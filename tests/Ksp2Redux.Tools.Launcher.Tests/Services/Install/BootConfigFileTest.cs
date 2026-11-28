using System.Text;
using Ksp2Redux.Tools.Common.Restoration;

namespace Ksp2Redux.Tools.Launcher.Tests.Services.Install;

/// <summary>
/// Line editing of the player's boot.config keeps every line it does not manage.
/// </summary>
public class BootConfigFileTest
{
    private static string Edit(string text, params (string Key, string? Value)[] changes) =>
        Encoding.Latin1.GetString(BootConfigFile.Edit(Encoding.Latin1.GetBytes(text),
            changes.ToDictionary(change => change.Key, change => change.Value)));

    /// <summary>
    /// Setting, changing and deleting keys keeps the other lines in their order.
    /// </summary>
    [Test]
    public void SetModifyAndDelete_KeepOtherLinesVerbatimAndInOrder()
    {
        Assert.That(Edit("a=1\nb=2\nc=3\n", ("b", "9")), Is.EqualTo("a=1\nb=9\nc=3\n"));
        Assert.That(Edit("a=1\nb=2\nc=3\n", ("a", null), ("b", "9"), ("d", "4")), Is.EqualTo("b=9\nc=3\nd=4\n"));
        Assert.That(Edit("a=1\nb=2\n", ("missing", null)), Is.EqualTo("a=1\nb=2\n"));
    }

    /// <summary>
    /// Comments, blank lines and lines without a key are kept as they are, and so is a value with spaces.
    /// </summary>
    [Test]
    public void UnknownAndMalformedLines_AreKept()
    {
        const string text = "# note\n\nnovalue\n=x\n  =y\nkey=a b \n";
        Assert.That(Edit(text, ("key", "2")), Is.EqualTo("# note\n\nnovalue\n=x\n  =y\nkey=2\n"));
        Assert.That(Edit(text, ("key", null)), Is.EqualTo("# note\n\nnovalue\n=x\n  =y\n"));
    }

    /// <summary>
    /// A key matches with whitespace around it, and a line that is rewritten is written as key=value.
    /// </summary>
    [Test]
    public void SpacedKeys_AreMatchedAndRewrittenAsKeyEqualsValue()
    {
        Assert.That(Edit("gfx = 0\nkeep=1\n", ("gfx", "1")), Is.EqualTo("gfx=1\nkeep=1\n"));
        Assert.That(Edit(" gfx= 1\nkeep=1\n", ("gfx", "1")), Is.EqualTo("gfx=1\nkeep=1\n"));
        Assert.That(Edit("gfx =0\nkeep=1\ngfx=2\n", ("gfx", null)), Is.EqualTo("keep=1\n"));
        Assert.That(BootConfigFile.ReadSettings("spaced = 3 \r\n"u8.ToArray()),
            Is.EqualTo(new Dictionary<string, string> { ["spaced"] = "3" }));
    }

    /// <summary>
    /// A lone carriage return ends a line, so no line is lost, and every line keeps its own ending.
    /// </summary>
    [Test]
    public void CarriageReturnLineEndings_KeepEveryLine()
    {
        Assert.That(Edit("gfx-enable-gfx-jobs=0\rkeep=1\r", ("gfx-enable-gfx-jobs", "1")), Is.EqualTo("gfx-enable-gfx-jobs=1\rkeep=1\r"));
        Assert.That(Edit("a=1\rb=2\r", ("c", "3")), Is.EqualTo("a=1\rb=2\rc=3\r"));
        Assert.That(Edit("a=1\r\nb=2\rc=3\n", ("b", "9")), Is.EqualTo("a=1\r\nb=9\rc=3\n"));
        Assert.That(Edit("a=1\rb=2", ("b", null)), Is.EqualTo("a=1\r"));
    }

    /// <summary>
    /// A new line follows the file's own line ending, and a last line without one gets one first.
    /// </summary>
    /// <param name="original">The file text.</param>
    /// <param name="expected">The file text after setting c.</param>
    [TestCase("a=1\r\nb=2\r\n", "a=1\r\nb=2\r\nc=3\r\n")]
    [TestCase("a=1\nb=2\n", "a=1\nb=2\nc=3\n")]
    [TestCase("a=1\nb=2", "a=1\nb=2\nc=3\n")]
    [TestCase("a=1\r\nb=2", "a=1\r\nb=2\r\nc=3\r\n")]
    [TestCase("", "c=3\n")]
    public void NewLine_FollowsTheFilesLineEnding(string original, string expected) =>
        Assert.That(Edit(original, ("c", "3")), Is.EqualTo(expected));

    /// <summary>
    /// A changed line keeps its own ending, and CRLF survives for lines the editor leaves alone.
    /// </summary>
    [Test]
    public void ChangedLine_KeepsItsOwnLineEnding() =>
        Assert.That(Edit("a=1\r\nb=2\nc=3\r\n", ("a", "5"), ("b", "6")), Is.EqualTo("a=5\r\nb=6\nc=3\r\n"));

    /// <summary>
    /// A leading UTF-8 signature is kept and does not hide the first key.
    /// </summary>
    [Test]
    public void Utf8Signature_IsKeptAndDoesNotHideTheFirstKey()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. "gfx-enable-gfx-jobs=1\r\nother=1\r\n"u8];
        byte[] edited = BootConfigFile.Edit(original, new Dictionary<string, string?> { ["gfx-enable-gfx-jobs"] = "0" });
        Assert.That(edited, Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }.Concat("gfx-enable-gfx-jobs=0\r\nother=1\r\n"u8.ToArray())));
        Assert.That(BootConfigFile.ReadSettings(original)["gfx-enable-gfx-jobs"], Is.EqualTo("1"));
    }

    /// <summary>
    /// The first line of a managed key is changed and later duplicates are removed, or all are removed on deletion.
    /// </summary>
    [Test]
    public void DuplicateKeys_FirstChangedLaterRemoved()
    {
        Assert.That(Edit("k=1\nx=1\nk=2\nk=3\n", ("k", "9")), Is.EqualTo("k=9\nx=1\n"));
        Assert.That(Edit("k=1\nx=1\nk=2\n", ("k", null)), Is.EqualTo("x=1\n"));
        Assert.That(Edit("k=9\nx=1\nk=9\n", ("k", "9")), Is.EqualTo("k=9\nx=1\n"), "A repeated line is still a change.");
    }

    /// <summary>
    /// An edit that changes nothing returns the same bytes, so callers can skip the write.
    /// </summary>
    [Test]
    public void EditWithoutChange_ReturnsTheSameArray()
    {
        byte[] original = "a=1\r\nb=2\r\n"u8.ToArray();
        Assert.That(BootConfigFile.Edit(original, new Dictionary<string, string?> { ["a"] = "1", ["gone"] = null }), Is.SameAs(original));
    }

    /// <summary>
    /// Bytes that are no valid text stay exactly as they were on lines the editor does not manage.
    /// </summary>
    [Test]
    public void UnreadableBytes_SurviveByteForByte()
    {
        byte[] original = [0xFF, 0xFE, (byte)'=', 0x80, 0x0D, 0x0A, (byte)'a', (byte)'=', (byte)'1', 0x0D, 0x0A];
        byte[] edited = BootConfigFile.Edit(original, new Dictionary<string, string?> { ["b"] = "2" });
        Assert.That(edited, Is.EqualTo(original.Concat("b=2\r\n"u8.ToArray())));
    }

    /// <summary>
    /// A release carries every valid key and value except the build identity.
    /// </summary>
    [Test]
    public void ReadSettings_SkipsBuildGuidAndMalformedLines()
    {
        byte[] file = "wait=0\r\nbuild-guid=8ea2\r\nno equals\r\n=bad\r\nwait=7\r\nenable-directstorage=True\r\n"u8.ToArray();
        Assert.That(BootConfigFile.ReadSettings(file),
            Is.EqualTo(new Dictionary<string, string> { ["wait"] = "0", ["enable-directstorage"] = "True" }));
    }

    /// <summary>
    /// A release setting needs a key and a value without line breaks, and its key has no equals sign or whitespace.
    /// </summary>
    /// <param name="key">The setting name.</param>
    /// <param name="value">The setting value.</param>
    /// <param name="valid">Whether the pair is usable.</param>
    [TestCase("gfx-threading-mode", "6", true)]
    [TestCase("key", "", true)]
    [TestCase("key", "a=b", true)]
    [TestCase(null, "1", false)]
    [TestCase("", "1", false)]
    [TestCase("key", null, false)]
    [TestCase("a=b", "1", false)]
    [TestCase("a\nb", "1", false)]
    [TestCase("a\rb", "1", false)]
    [TestCase("key", "a\nb", false)]
    [TestCase("key", "a\rb", false)]
    [TestCase("build-guid", "8ea2", false)]
    public void IsValidSetting_AcceptsOnlyUsablePairs(string? key, string? value, bool valid) =>
        Assert.That(BootConfigFile.IsValidSetting(key, value), Is.EqualTo(valid));

    /// <summary>
    /// The stock set is the three storefront settings, and nothing else, build-guid included, has a stock value.
    /// </summary>
    [Test]
    public void StockSettings_AreTheThreeStorefrontSettings()
    {
        Assert.That(BootConfigFile.StockSettings, Is.EqualTo(new Dictionary<string, string>
        {
            ["wait-for-native-debugger"] = "0", ["hdr-display-enabled"] = "0", ["gc-max-time-slice"] = "3"
        }));
        Assert.That(BootConfigFile.StockValue("build-guid"), Is.Null);
        Assert.That(BootConfigFile.StockValue("gfx-enable-gfx-jobs"), Is.Null);
    }
}
