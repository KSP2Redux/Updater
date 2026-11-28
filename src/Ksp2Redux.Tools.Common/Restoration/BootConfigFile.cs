using System.Text;

namespace Ksp2Redux.Tools.Common.Restoration;

/// <summary>
/// Names the player's boot.config as the one file no full-source contract lists, and edits it line by line.
/// </summary>
/// <remarks>
/// The launcher decides this file outside every transaction. It never enters a contract, a hash comparison, a
/// recipe, a stage, a backup or a journal. Editing keeps every unmanaged line and the file's own layout, and never
/// throws for content it does not understand.
/// </remarks>
public static class BootConfigFile
{
    /// <summary>
    /// The contract path of the player's boot.config.
    /// </summary>
    public const string RELATIVE_PATH = "KSP2_x64_Data/boot.config";

    /// <summary>
    /// The build identity line of a Unity player. It differs for every build, so it is never a release setting.
    /// </summary>
    public const string BUILD_GUID_KEY = "build-guid";

    /// <summary>
    /// The settings every storefront build of the game ships with, besides the build identity, and their stock values.
    /// </summary>
    /// <remarks>
    /// Steam, Epic and portable copies carry exactly these lines and a build-guid line. Any other key is absent
    /// from a stock file. The settings step records this state, not the file's current content, as the original.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> StockSettings { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["wait-for-native-debugger"] = "0",
        ["hdr-display-enabled"] = "0",
        ["gc-max-time-slice"] = "3"
    };

    /// <summary>
    /// The most settings a release can carry.
    /// </summary>
    public const int MAX_SETTINGS = 256;

    /// <summary>
    /// The largest boot.config the publisher reads.
    /// </summary>
    public const int MAX_BYTES = 64 * 1024;

    private static readonly byte[] BOM = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Get whether a contract path names the boot.config that contracts leave out.
    /// </summary>
    /// <param name="path">The relative contract path.</param>
    /// <returns>True for the player's boot.config.</returns>
    public static bool IsExcluded(string path) =>
        path.Replace('\\', '/').Equals(RELATIVE_PATH, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Get the value a key has in a stock file.
    /// </summary>
    /// <param name="key">The setting name.</param>
    /// <returns>The stock value, or null when a stock file has no such line.</returns>
    public static string? StockValue(string key) => StockSettings.GetValueOrDefault(key);

    /// <summary>
    /// Get whether a key and value can be written as a release setting.
    /// </summary>
    /// <param name="key">The setting name.</param>
    /// <param name="value">The setting value.</param>
    /// <returns>
    /// True when both exist and are bounded text. The key is not empty, holds no line break, no equals sign and no
    /// whitespace, and is not the build identity. The value holds no line break or control character.
    /// </returns>
    public static bool IsValidSetting(string? key, string? value) =>
        key is { Length: > 0 and <= 256 } && value is { Length: <= 1024 } &&
        !key.Equals(BUILD_GUID_KEY, StringComparison.Ordinal) &&
        !key.Any(character => character == '=' || char.IsControl(character) || char.IsWhiteSpace(character)) &&
        !value.Any(char.IsControl);

    /// <summary>
    /// Read the settings of a player's boot.config for a release descriptor.
    /// </summary>
    /// <remarks>
    /// Lines without a key, the build identity and unusable text are left out. The first line of a key wins.
    /// </remarks>
    /// <param name="content">The file bytes.</param>
    /// <returns>The keys and values in file order.</returns>
    public static Dictionary<string, string> ReadSettings(byte[] content)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (text, _) in Split(content, out _))
        {
            if (KeyOf(text) is not { } key)
                continue;
            string value = Text(text[(text.IndexOf('=') + 1)..].Trim());
            if (IsValidSetting(Text(key), value))
                settings.TryAdd(Text(key), value);
        }
        return settings;
    }

    /// <summary>
    /// Set, change or delete keys, and keep everything else as it is.
    /// </summary>
    /// <remarks>
    /// A line matches a key when the text before its first equals sign equals the key once whitespace around it is
    /// trimmed. The first line of a managed key is changed, rewritten as key=value, or removed, and its later
    /// duplicates are removed. A key without a line is appended in the file's line ending style. Lines without a
    /// key are kept. A leading UTF-8 signature is kept. Bytes the editor does not change are never re-encoded.
    /// Line breaks are LF, CRLF or a lone CR.
    /// </remarks>
    /// <param name="content">The current file bytes.</param>
    /// <param name="changes">The keys to set, or to delete when the value is null.</param>
    /// <returns>The same array when nothing changes, otherwise the new file bytes.</returns>
    public static byte[] Edit(byte[] content, IReadOnlyDictionary<string, string?> changes)
    {
        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in changes)
            wanted[Raw(key)] = value is null ? null : Raw(value);

        var lines = Split(content, out int signature);
        string newline = lines.FirstOrDefault(line => line.End.Length != 0).End ?? "\n";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string Text, string End)>(lines.Count + wanted.Count);
        bool changed = false;
        foreach (var line in lines)
        {
            string key = KeyOf(line.Text) ?? "";
            if (key.Length == 0 || !wanted.TryGetValue(key, out string? value))
            {
                result.Add(line);
            }
            else if (!seen.Add(key) || value is null)
            {
                changed = true;
            }
            else
            {
                string replaced = $"{key}={value}";
                changed |= replaced != line.Text;
                result.Add((replaced, line.End));
            }
        }
        foreach (var (key, value) in wanted)
        {
            if (value is null || seen.Contains(key))
                continue;
            if (result.Count != 0 && result[^1].End.Length == 0)
                result[^1] = (result[^1].Text, newline);
            result.Add(($"{key}={value}", newline));
            changed = true;
        }
        if (!changed)
            return content;

        var text = new StringBuilder();
        foreach (var (lineText, end) in result)
            text.Append(lineText).Append(end);
        byte[] body = Encoding.Latin1.GetBytes(text.ToString());
        return signature == 0 ? body : [.. BOM, .. body];
    }

    // The text before the first equals sign without surrounding whitespace, or null when the line has no key.
    private static string? KeyOf(string line)
    {
        int equals = line.IndexOf('=');
        if (equals <= 0)
            return null;
        string key = line[..equals].Trim();
        return key.Length == 0 ? null : key;
    }

    // The Latin-1 form maps every byte to one character, so lines the editor leaves alone survive unchanged.
    private static List<(string Text, string End)> Split(byte[] content, out int signature)
    {
        signature = content.AsSpan().StartsWith(BOM) ? BOM.Length : 0;
        string text = Encoding.Latin1.GetString(content, signature, content.Length - signature);
        var lines = new List<(string Text, string End)>();
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\n' or '\r'))
                continue;
            int length = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
            lines.Add((text[start..index], text.Substring(index, length)));
            index += length - 1;
            start = index + 1;
        }
        if (start < text.Length)
            lines.Add((text[start..], ""));
        return lines;
    }

    private static string Raw(string value) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(value));

    private static string Text(string raw) => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(raw));
}
