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
}

public class ExecutingAssemblyService : IAssemblyService
{
    public Stream? GetManifestResourceStream(string name)
        => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    public AssemblyName GetName()
        => Assembly.GetExecutingAssembly().GetName();
}