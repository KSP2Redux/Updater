
using Mono.Cecil;

namespace Ksp2Redux.Tools.Launcher.Services;

public interface IModuleDefinitionService
{
    ModuleDefinition ReadModule(string fileName);
}

public class ModuleDefinitionService : IModuleDefinitionService
{
    public ModuleDefinition ReadModule(string fileName)
        => ModuleDefinition.ReadModule(fileName);
}