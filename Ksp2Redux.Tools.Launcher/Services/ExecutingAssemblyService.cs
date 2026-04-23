using System;
using System.IO;
using System.Reflection;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IAssemblyService
{
    // Interface to make wrappers of System.Reflection.Assembly
    // Only covers members that are currently used
    // Feel free to add other members if needed
    
    Stream? GetManifestResourceStream(string name);
    AssemblyName GetName();
    
    Version? GetVersion();
}

public class ExecutingAssemblyService : IAssemblyService
{
#pragma warning disable RS0030
    
    public Stream? GetManifestResourceStream(string name)
        => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    public AssemblyName GetName()
        => Assembly.GetExecutingAssembly().GetName();

    public Version? GetVersion()
        => Assembly.GetExecutingAssembly().GetName().Version;

#pragma warning restore RS0030
}