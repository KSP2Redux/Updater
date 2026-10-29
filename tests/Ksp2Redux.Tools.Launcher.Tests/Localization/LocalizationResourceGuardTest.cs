using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ksp2Redux.Tools.Launcher.Tests.Localization;

/// <summary>
/// Keeps the code, the English resx and the translation resx files the pipeline writes later in step.
/// </summary>
public partial class LocalizationResourceGuardTest
{
    private const string NeutralFileName = "Strings.resx";

    private static readonly string LauncherDirectory = FindLauncherDirectory();
    private static readonly string LocalizationDirectory = Path.Combine(LauncherDirectory, "Localization");

    [GeneratedRegex(@"\{loc:T\s+([^\s}]+)\}")]
    private static partial Regex MarkupKey();

    [GeneratedRegex("\"([A-Z][A-Za-z0-9]*(?:\\.[A-Z][A-Za-z0-9]*)+)\"")]
    private static partial Regex DottedLiteral();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    [Test]
    public void Code_UsesOnlyKeysTheNeutralResxDefines()
    {
        var keys = ReadResx(Path.Combine(LocalizationDirectory, NeutralFileName)).Keys.ToHashSet();
        var areas = keys.Select(key => key[..key.IndexOf('.')]).ToHashSet();
        List<string> unknown = [];

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            var name = Path.GetRelativePath(LauncherDirectory, file);
            if (file.EndsWith(".axaml"))
            {
                unknown.AddRange(MarkupKey().Matches(text).Select(match => match.Groups[1].Value)
                    .Where(key => !keys.Contains(key)).Select(key => $"{name}: {key}"));
                continue;
            }

            // A literal that starts like a key may be a prefix, because some keys are completed in code.
            unknown.AddRange(DottedLiteral().Matches(text).Select(match => match.Groups[1].Value)
                .Where(literal => areas.Contains(literal[..literal.IndexOf('.')]))
                .Where(literal => !keys.Contains(literal) && !keys.Any(key => key.StartsWith(literal + ".")))
                .Select(literal => $"{name}: {literal}"));
        }

        Assert.That(unknown, Is.Empty, "These keys are used but missing from Strings.resx");
    }

    [Test]
    public void NeutralResx_HasNoEntryNothingUses()
    {
        HashSet<string> literals = [];
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            literals.UnionWith(file.EndsWith(".axaml")
                ? MarkupKey().Matches(text).Select(match => match.Groups[1].Value)
                : DottedLiteral().Matches(text).Select(match => match.Groups[1].Value));
        }

        // A key is also used when code completes a literal prefix with a suffix.
        var unused = ReadResx(Path.Combine(LocalizationDirectory, NeutralFileName)).Keys
            .Where(key => !literals.Contains(key) && !literals.Any(literal => key.StartsWith(literal + ".")));

        Assert.That(unused, Is.Empty, "Translators would translate these for nothing");
    }

    [Test]
    public void NeutralResx_GivesEveryEntryAComment()
    {
        var document = XDocument.Load(Path.Combine(LocalizationDirectory, NeutralFileName));
        var withoutComment = document.Root!.Elements("data")
            .Where(data => string.IsNullOrWhiteSpace(data.Element("comment")?.Value))
            .Select(data => data.Attribute("name")!.Value);

        Assert.That(withoutComment, Is.Empty, "Translators read these comments in Weblate");
    }

    [Test]
    public void Translations_KeepThePlaceholdersOfTheNeutralEntry()
    {
        var neutral = ReadResx(Path.Combine(LocalizationDirectory, NeutralFileName));
        List<string> problems = [];

        // Empty until the pipeline copies translations in.
        foreach (var file in Directory.EnumerateFiles(LocalizationDirectory, "Strings.*.resx"))
        {
            problems.AddRange(FindPlaceholderMismatches(neutral, ReadResx(file)).Select(problem => $"{Path.GetFileName(file)}: {problem}"));
        }

        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void FindPlaceholderMismatches_ReportsChangedMissingAndStaleEntries()
    {
        var neutral = new Dictionary<string, string>
        {
            ["Two"] = "{0} of {1}",
            ["Formatted"] = "{0:F1} MB",
            ["Plain"] = "text",
            ["Escaped"] = "{{0}} braces"
        };
        var translated = new Dictionary<string, string>
        {
            ["Two"] = "{0}",
            ["Formatted"] = "{0:0.0} Mo",
            ["Plain"] = "{0}",
            ["Escaped"] = "{{0}} accolades",
            ["Removed"] = "stale"
        };

        var problems = FindPlaceholderMismatches(neutral, translated);

        Assert.That(problems, Has.Count.EqualTo(3));
        Assert.That(problems, Has.One.StartsWith("Two:"));
        Assert.That(problems, Has.One.StartsWith("Plain:"));
        Assert.That(problems, Has.One.StartsWith("Removed:"));
    }

    private static List<string> FindPlaceholderMismatches(
        IReadOnlyDictionary<string, string> neutral, IReadOnlyDictionary<string, string> translated)
    {
        List<string> problems = [];
        foreach (var (key, text) in translated)
        {
            if (!neutral.TryGetValue(key, out var original))
            {
                problems.Add($"{key}: not in {NeutralFileName}");
            }
            else if (!PlaceholderIndices(original).SequenceEqual(PlaceholderIndices(text)))
            {
                problems.Add($"{key}: placeholders {{{string.Join(", ", PlaceholderIndices(text))}}} differ from {{{string.Join(", ", PlaceholderIndices(original))}}}");
            }
        }

        return problems;
    }

    private static int[] PlaceholderIndices(string text) =>
        Placeholder().Matches(text.Replace("{{", "").Replace("}}", ""))
            .Select(match => int.Parse(match.Groups[1].Value)).Distinct().Order().ToArray();

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(data => data.Attribute("name")!.Value, data => data.Element("value")?.Value ?? string.Empty);

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(LauncherDirectory, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs") || file.EndsWith(".axaml"))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));

    private static string FindLauncherDirectory()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Ksp2Redux.Tools.Launcher");
            if (File.Exists(Path.Combine(candidate, "Localization", NeutralFileName))) return candidate;
        }

        throw new DirectoryNotFoundException("Could not find src/Ksp2Redux.Tools.Launcher above the test output.");
    }
}
