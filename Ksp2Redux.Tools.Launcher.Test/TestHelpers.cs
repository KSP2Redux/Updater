using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Ksp2Redux.Tools.Launcher.Test.HeadlessTests;
using Mono.Cecil;
using Moq;
using MsBox.Avalonia.Enums;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

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

    public static void MockKsp2StockSteamInstall()
    {
        TestAppBuilder.EnvironmentProvider.SetFolderPath(Environment.SpecialFolder.LocalApplicationData, @"C:\AppDataLocal");
        TestAppBuilder.FileSystem.Directory.CreateDirectory(@"C:\AppDataLocal");
        TestAppBuilder.FileSystem.Directory.CreateDirectory(TestAppBuilder.FileSystem.Path.GetTempPath());

        string libraryFoldersFileContent = """
                                          "libraryfolders"
                                          {
                                          	"0"
                                          	{
                                          		"path"		"C:\\Program Files (x86)\\Steam"
                                          	}
                                          }
                                          """;
        TestAppBuilder.FileSystem.Directory.CreateDirectory(@"C:\Program Files (x86)\Steam\steamapps");
        TestAppBuilder.FileSystem.File.WriteAllText(@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf", libraryFoldersFileContent);
        
        string appmanifestFileContent = """
                                        "AppState"
                                        {
                                        	"installdir"		"Kerbal Space Program 2"
                                        }
                                        """;
        TestAppBuilder.FileSystem.File.WriteAllText(@"C:\Program Files (x86)\Steam\steamapps\appmanifest_954850.acf", appmanifestFileContent);

        using Stream? embeddedResourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Ksp2Redux.Tools.Launcher.Test.MockGame.MockGame.exe");
        
        if(embeddedResourceStream == null)
            Assert.Fail("Embedded resource not found");
        
        using StreamReader reader  = new(embeddedResourceStream!);
        
        TestAppBuilder.FileSystem.Directory.CreateDirectory(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2");
        TestAppBuilder.FileSystem.File.WriteAllText(
            @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64.exe",
            reader.ReadToEnd());
        
        var gameExeModule = TestHelpers.GenerateMockVersionID(
            ("VERSION_TEXT", "0.2.2.0.32914"),
            ("DEBUG_INFO", "BUILD_INFO")
        );

        TestAppBuilder.ModuleDefinitionService
            .Setup(m => m.ReadModule(
                @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64_Data\Managed\Assembly-CSharp.dll"))
            .Returns(gameExeModule.module);
        
        TestAppBuilder.FileSystem.Directory.CreateDirectory(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64_Data\Plugins");
        TestAppBuilder.FileSystem.File.WriteAllText(@"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2\KSP2_x64_Data\Plugins\Steamworks.NET.txt", 
            "");
    }

    public static void MockMessageBoxAcceptAll()
    {
        TestAppBuilder.MessageBoxService.Setup(m => m.ShowMessageBoxAsOwnedAsync(
                It.IsAny<string>(), It.IsAny<string>(), 
                It.Is<ButtonEnum>(b => b == ButtonEnum.Ok || b == ButtonEnum.OkAbort || b == ButtonEnum.OkCancel),
                It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()
            ))
            .ReturnsAsync(ButtonResult.Ok);
        TestAppBuilder.MessageBoxService.Setup(m => m.ShowMessageBoxAsOwnedAsync(
                It.IsAny<string>(), It.IsAny<string>(), 
                It.Is<ButtonEnum>(b => b == ButtonEnum.YesNo || b == ButtonEnum.YesNoAbort || b == ButtonEnum.YesNoCancel),
                It.IsAny<Icon>(), It.IsAny<object>(), It.IsAny<WindowStartupLocation>()
            ))
            .ReturnsAsync(ButtonResult.Yes);
    }

    public static byte[] GetDiff(string oldContent, string newContent)
        => GetDiff(Encoding.ASCII.GetBytes(oldContent), Encoding.ASCII.GetBytes(newContent));
    public static byte[] GetDiff(byte[] oldContent, string newContent)
        => GetDiff(oldContent, Encoding.ASCII.GetBytes(newContent));
    public static byte[] GetDiff(byte[] oldContent, byte[] newContent)
    {
        MemoryStream output = new();
        BsDiff.BinaryPatch.Create(oldContent, newContent, output);
        return output.ToArray();
    }

    public static string DescribeFoldersAndFiles(string root)
    {
        return "\t- " + string.Join("\n\t- ",
            TestAppBuilder.FileSystem.Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories));
    }
}