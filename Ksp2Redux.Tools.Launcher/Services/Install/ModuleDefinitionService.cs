using Mono.Cecil;

namespace Ksp2Redux.Tools.Launcher.Services.Install;

public interface IModuleDefinitionService
{
    ModuleDefinition ReadModule(string fileName);
}

public class ModuleDefinitionService : IModuleDefinitionService
{
#pragma warning disable RS0030
    
    public ModuleDefinition ReadModule(string fileName)
        => ModuleDefinition.ReadModule(fileName, new ReaderParameters
        {
            InMemory = true,
        });
    
#pragma warning restore RS0030
}