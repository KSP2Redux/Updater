// See https://aka.ms/new-console-template for more information

using System.IO.Abstractions;
using Ksp2Redux.Tools.Common;
using Ksp2Redux.Tools.Common.Service;
using Testably.Abstractions;

Console.WriteLine(string.Join(", ",args));

var ksp2Directory = args[0];
var buildDirectory = args[1];
var result = args[2];
var checkRemovals = args.Length > 3 && args[3] == "true";

IFileSystem fileSystem = new RealFileSystem();
IZipFileService zipFileService = new ZipFileService(fileSystem);

Patch();
Dump();

return;

void Patch()
{
    using var _ = Ksp2Patch.FromDiff(fileSystem, result, ksp2Directory, buildDirectory, checkRemovals);
}

void Dump()
{
    using var patch = Ksp2Patch.FromFile(fileSystem, zipFileService, result);
    Console.Write(patch.GetDiffInfo());
}

