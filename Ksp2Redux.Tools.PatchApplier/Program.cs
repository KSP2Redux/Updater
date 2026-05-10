using System.IO.Abstractions;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Common.Service;
using Testably.Abstractions;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Ksp2Redux.Tools.PatchApplier <patchFile> <targetDir>");
    return 1;
}

var patchFile = args[0];
var targetDir = args[1];

IFileSystem fileSystem = new RealFileSystem();
IZipFileService zipFileService = new ZipFileService(fileSystem);

using var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, patchFile);

var hadError = false;
await patch.AsyncApply(
    SystemEnvironmentProvider.Instance,
    targetDir,
    log: Console.WriteLine,
    error: msg =>
    {
        hadError = true;
        Console.Error.WriteLine(msg);
    }
);

return hadError ? 1 : 0;
