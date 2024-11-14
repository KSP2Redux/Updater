// See https://aka.ms/new-console-template for more information

using Ksp2Redux.Tools.Common;

Console.WriteLine(string.Join(", ",args));

var ksp2Directory = args[0];
var buildDirectory = args[1];
var result = args[2];

Patch();
Dump();

void Patch()
{
   using var _ = Ksp2Patch.FromDiff(result, ksp2Directory, buildDirectory);
}

void Dump()
{
    using var patch = Ksp2Patch.FromFile(result);
    Console.Write(patch.GetDiffInfo());
}

