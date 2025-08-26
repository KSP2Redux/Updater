using System;
using System.Reflection;

namespace Ksp2Redux.Tools.Launcher.Models;

public class GameVersion : IEquatable<GameVersion>
{
    public ReleaseChannel Channel { get; set; }
    public required Version VersionNumber { get; set; }
    public required string BuildNumber { get; set; }
    public string? CommitHash { get; set; }

    /// <summary>
    /// Read version data VersionID class constants in Assembly-CSharp.dll
    /// </summary>
    public static GameVersion FromVersionIDType(Type versionType)
    {
        ReleaseChannel channel = ReleaseChannel.Stable;
        Version version;
        string buildNumber;
        string commitHash;

        // VERSION_TEXT is common between redux and stock.
        // Stock: "0.2.2.0.32914"
        var versionText = versionType.GetField("VERSION_TEXT")!.GetValue(null) as string;
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

        // try get redux commit hash
        if (versionType.GetField("DEBUG_INFO")?.GetValue(null) is string possibleHash && possibleHash != "BUILD_INFO")
        {
            commitHash = possibleHash;
        }
        else
        {
            commitHash = string.Empty;
        }

        if (versionType.GetField("CHANNEL_NAME") is FieldInfo channelNameField)
        {
            var channelName = channelNameField.GetValue(null) as string;
            if (channelName == "beta")
            {
                channel = ReleaseChannel.Beta;
            }
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
}