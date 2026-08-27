// See https://aka.ms/new-console-template for more information

using System.IO.Abstractions;
using Ksp2Redux.Tools.Common.Patching;
using Ksp2Redux.Tools.Common.Services;
using Testably.Abstractions;

Console.WriteLine(string.Join(", ",args));

var ksp2Directory = args[0];
var buildDirectory = args[1];
var result = args[2];
var checkRemovals = args.Length > 3 && args[3] == "true";
var removalRoots = ParseRemovalRoots(args.Skip(4));

IFileSystem fileSystem = new RealFileSystem();
IZipFileService zipFileService = new ZipFileService(fileSystem);

Patch();
Dump();

return;

void Patch()
{
    using var _ = Ksp2Patch.FromDiff(
        fileSystem,
        result,
        ksp2Directory,
        buildDirectory,
        checkRemovals,
        removalRoots);
}

void Dump()
{
    using var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, result);
    Console.Write(patch.GetDiffInfo());
}

static List<string> ParseRemovalRoots(IEnumerable<string> arguments)
{
    var removals = new List<string>();
    using IEnumerator<string> enumerator = arguments.GetEnumerator();
    while (enumerator.MoveNext())
    {
        if (enumerator.Current != "--remove-missing-dlls-under" || !enumerator.MoveNext())
        {
            throw new ArgumentException("Expected '--remove-missing-dlls-under <relative directory>'.");
        }

        removals.Add(enumerator.Current);
    }

    return removals;
}

