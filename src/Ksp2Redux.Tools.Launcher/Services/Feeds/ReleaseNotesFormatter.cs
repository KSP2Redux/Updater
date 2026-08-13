using System.Text;
using System.Text.RegularExpressions;

namespace Ksp2Redux.Tools.Launcher.Services.Feeds;

/// <summary>
/// Turns a GitHub release body into something worth showing in a message box.
/// </summary>
// The release body is markdown written for the releases page, where GitHub renders it. The update
// dialog is a plain text control, so without this the user reads hashes, asterisks, pipe tables and
// full URLs.
public static partial class ReleaseNotesFormatter
{
    /// <summary>
    /// Everything after this marker in a release body is for the web page rather than the launcher.
    /// </summary>
    public const string LAUNCHER_CUTOFF = "<!-- launcher-notes-end -->";

    /// <summary>
    /// Reduces a release body to plain text, keeping the words and dropping the markup.
    /// </summary>
    /// <param name="markdown">The release body, or null.</param>
    /// <returns>Plain text, empty when there was nothing worth showing.</returns>
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var text = markdown.ReplaceLineEndings("\n");

        var cutoff = text.IndexOf(LAUNCHER_CUTOFF, StringComparison.OrdinalIgnoreCase);
        if (cutoff >= 0)
        {
            text = text[..cutoff];
        }

        text = HtmlComment().Replace(text, "");

        StringBuilder plain = new();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();

            // A pipe table and a horizontal rule carry no meaning once the markup is gone.
            if (line.TrimStart().StartsWith('|') || HorizontalRule().IsMatch(line))
            {
                continue;
            }

            line = Heading().Replace(line, "");
            line = Bullet().Replace(line, "- ");
            line = Link().Replace(line, "$1");
            line = PullRequestUrl().Replace(line, "#$1");
            line = CompareUrl().Replace(line, "$1");
            line = Emphasis().Replace(line, "$2");
            line = InlineCode().Replace(line, "$1");

            plain.Append(line.TrimEnd()).Append('\n');
        }

        return BlankRun().Replace(plain.ToString(), "\n\n").Trim();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*[*+-]\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"https://github\.com/[^/\s]+/[^/\s]+/(?:pull|issues)/(\d+)")]
    private static partial Regex PullRequestUrl();

    // Nothing in a message box is clickable, so a compare link is only worth the versions it spans.
    [GeneratedRegex(@"https://github\.com/[^/\s]+/[^/\s]+/compare/(\S+)")]
    private static partial Regex CompareUrl();

    // Only asterisks. An underscore pair is valid markdown emphasis, but a release note is far more
    // likely to be naming a file like KSP2_x64_Data than italicising something.
    [GeneratedRegex(@"(\*\*|\*)(.+?)\1")]
    private static partial Regex Emphasis();

    [GeneratedRegex("`([^`]*)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRun();
}
