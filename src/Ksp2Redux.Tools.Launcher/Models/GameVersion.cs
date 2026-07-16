using Mono.Cecil;

namespace Ksp2Redux.Tools.Launcher.Models;

public class GameVersion : IEquatable<GameVersion>
{
    public string Channel { get; set; } = "stable";
    public required Version VersionNumber { get; set; }
    public required string BuildNumber { get; set; }
    public string? CommitHash { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? Label { get; set; }

    /// <summary>
    /// Read version data VersionID class constants in Assembly-CSharp.dll
    /// </summary>
    public static GameVersion FromVersionIDType(TypeDefinition versionType, bool isRedux)
    {
        string channel = "stable";
        Version version;
        string buildNumber;
        string commitHash;

        string GetRequiredFieldValueAsString(string fieldName)
        {
            var field = versionType.Fields.FirstOrDefault(f => f.Name == fieldName);
            if (field?.Constant is not { } value)
                throw new InvalidOperationException($"VersionID field '{fieldName}' is missing or null.");

            var text = value.ToString();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"VersionID field '{fieldName}' is empty.");

            return text;
        }

        // VERSION_TEXT is common between redux and stock.
        // Stock: "0.2.2.0.32914"
        var versionText = GetRequiredFieldValueAsString("VERSION_TEXT");
        var tokens = versionText.Split('.');
        if (tokens.Length != 5)
        {
            throw new InvalidOperationException($"VERSION_TEXT must contain five dot-separated tokens: '{versionText}'.");
        }

        try
        {
            version = Version.Parse(string.Join('.', tokens[0..4]));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidOperationException($"VERSION_TEXT has an invalid version number: '{versionText}'.", ex);
        }

        buildNumber = tokens[4];
        if (string.IsNullOrWhiteSpace(buildNumber))
            throw new InvalidOperationException($"VERSION_TEXT has an invalid build number: '{versionText}'.");

        if (isRedux)
            channel = GetRequiredFieldValueAsString("CHANNEL_NAME");

        // try get redux commit hash
        var possibleHash = GetRequiredFieldValueAsString("DEBUG_INFO");
        if (possibleHash != "BUILD_INFO")
        {
            commitHash = $"{channel.ToLowerInvariant()}+{possibleHash}";
        }
        else
        {
            commitHash = string.Empty;
        }

        return new GameVersion()
        {
            Channel = channel,
            VersionNumber = version,
            BuildNumber = buildNumber,
            CommitHash = commitHash
        };
    }

    public bool Equals(GameVersion? other)
    {
        return other is not null
            && VersionNumber == other.VersionNumber
            && BuildNumber == other.BuildNumber;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as GameVersion);
    }

    public override int GetHashCode()
    {
        return BuildNumber.GetHashCode();
    }

    public override string ToString()
        => $"{VersionNumber}.{BuildNumber} ({Channel})";
}
