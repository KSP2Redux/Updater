using Mono.Cecil;
using System;
using System.Linq;

namespace Ksp2Redux.Tools.Launcher.Models;

public class GameVersion : IEquatable<GameVersion>
{
    public string Channel { get; set; }
    public required Version VersionNumber { get; set; }
    public required string BuildNumber { get; set; }
    public string? CommitHash { get; set; }
    public DateTime? ReleasedAt { get; set; }

    /// <summary>
    /// Read version data VersionID class constants in Assembly-CSharp.dll
    /// </summary>
    public static GameVersion FromVersionIDType(TypeDefinition versionType, bool IsRedux)
    {
        string channel = "stable";
        Version version;
        string buildNumber;
        string commitHash;

        string GetFieldValueAsString(string fieldName)
        {
            var buffer = versionType.Fields.First(f => f.Name == fieldName).Constant;
            return buffer.ToString()!;
        }

        // VERSION_TEXT is common between redux and stock.
        // Stock: "0.2.2.0.32914"
        var versionText = GetFieldValueAsString("VERSION_TEXT");
        if (!string.IsNullOrWhiteSpace(versionText))
        {
            var tokens = versionText.Split('.');
            version = Version.Parse(string.Join('.', tokens[0..4]));
            buildNumber = tokens[4];
        }
        else
        {
            version = new();
            buildNumber = string.Empty;
        }
        if(IsRedux)
            if (GetFieldValueAsString("CHANNEL_NAME") is { } channelName)
                channel = channelName;
        
        // try get redux commit hash
        if (GetFieldValueAsString("DEBUG_INFO") is { } possibleHash && possibleHash != "BUILD_INFO")
        {
            commitHash = $"{channel.ToLower()}+{possibleHash}";
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
            CommitHash = commitHash,
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