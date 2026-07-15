using System;
using System.Linq;
using Ksp2Redux.Tools.Common.Models;

namespace Ksp2Redux.Tools.Launcher.Models;

public static class ReleasePatchExtensions
{
    public static GameVersion ParseVersion(this ReleasePatch patch)
    {
        var tokens = patch.Version.Split(['.', '-']);
        // remove optional leading 'v' from version
        if (tokens[0][0] == 'v')
        {
            tokens[0] = tokens[0][1..];
        }

        Version versionNumber;
        string commitHash;
        if (tokens.Length > 4)
        {
            versionNumber = new Version(string.Join('.', tokens[0..4]));
            commitHash = tokens[4];
        }
        else
        {
            versionNumber = new Version(string.Join('.', tokens));
            commitHash = "0";
        }

        return new GameVersion
        {
            VersionNumber = versionNumber,
            BuildNumber = commitHash,
            CommitHash = commitHash
        };
    }
}
