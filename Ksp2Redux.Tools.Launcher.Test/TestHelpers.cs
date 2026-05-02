using Mono.Cecil;

namespace Ksp2Redux.Tools.Launcher.Test;

public static class TestHelpers
{
    public static (ModuleDefinition module, TypeDefinition type) GenerateMockVersionID(params List<(string name, string? value)> fields)
    {
        AssemblyDefinition? assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("MockAssembly", new Version(1, 0, 0, 0)),
            "MockModule", ModuleKind.Dll);
        ModuleDefinition? module = assembly.MainModule;

        TypeDefinition versionIDType = new(
            "", "VersionID",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);

        foreach (var fieldData in fields)
        {
            FieldDefinition field = new(
                fieldData.name,
                FieldAttributes.Public |
                FieldAttributes.Static |
                FieldAttributes.Literal |
                FieldAttributes.HasDefault,
                module.TypeSystem.String)
            {
                Constant = fieldData.value
            };
            versionIDType.Fields.Add(field);
        }

        module.Types.Add(versionIDType);
        return (module, versionIDType);
    }
}