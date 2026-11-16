using Ksp2Redux.Tools.Common.Services;
using System.IO.Abstractions;
using System.Text.Json;
using Ksp2Redux.Tools.Cli.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.Services.Install;
using Ksp2Redux.Tools.Launcher.Services.Mac;
using Ksp2Redux.Tools.Launcher.Services.News;
using Ksp2Redux.Tools.Launcher.Services.Steam;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Testably.Abstractions.Testing;

namespace Ksp2Redux.Tools.Launcher.Tests.Cli;

/// <summary>A CLI context over a mock file system with real config and install services, run in JSON mode with no terminal.</summary>
internal sealed class CliCommandHarness
{
    public const string STORAGE = @"C:\AppDataLocal";
    public const string HOME = @"C:\Users\Player";

    private CliContext? _context;

    public CliCommandHarness()
    {
        Environment.SetFolderPath(System.Environment.SpecialFolder.LocalApplicationData, STORAGE);
        Environment.SetFolderPath(System.Environment.SpecialFolder.ApplicationData, STORAGE);
        Environment.SetFolderPath(System.Environment.SpecialFolder.UserProfile, HOME);
        FileSystem.Directory.CreateDirectory(STORAGE);
        FileSystem.Directory.CreateDirectory(HOME);
        DiskSpace.Setup(d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        Output = new CliOutput(Results, isJson: true);
    }

    public MockFileSystem FileSystem { get; } = new(o => o.SimulatingOperatingSystem(SimulationMode.Windows));

    public MockEnvironmentProvider Environment { get; } = new();

    public Mock<IOperatingSystemService> OperatingSystem { get; } = new();

    public Mock<ISteamSessionService> Steam { get; } = new();

    public Mock<ISteamDepotDownloader> Downloader { get; } = new();

    public Mock<IWineRuntimeService> Wine { get; } = new();

    public Mock<IDiskSpaceService> DiskSpace { get; } = new();

    public Mock<INewsProviderService> News { get; } = new();

    public Mock<IGameDataFolderService> GameDataFolder { get; } = new();

    public Mock<IInstallPlanService> InstallPlan { get; } = new();

    public Mock<IModuleDefinitionService> Modules { get; } = new();

    public Mock<IManifestReleasesFeedProviderService> FeedProvider { get; } = new();

    public StringWriter Results { get; } = new();

    /// <summary>The output the context reports through, JSON over <see cref="Results" /> unless a test replaces it before first use.</summary>
    public CliOutput Output { get; set; }

    /// <summary>The context, built on first use so a test can arrange files and mocks before the config loads.</summary>
    public CliContext Context => _context ??= Build();

    public IKsp2InstallService Installs => Context.InstallService;

    public JsonElement Json => JsonDocument.Parse(Results.ToString()).RootElement;

    /// <summary>Lays out a KSP2 folder the launcher recognises as an install and returns its exe path.</summary>
    public string CreateGame(string folder)
    {
        FileSystem.Directory.CreateDirectory(FileSystem.Path.Combine(folder, "KSP2_x64_Data", "Managed"));
        var exe = FileSystem.Path.Combine(folder, "KSP2_x64.exe");
        FileSystem.File.WriteAllText(exe, "exe");
        return exe;
    }

    /// <summary>Makes every install read as the given stock version.</summary>
    public void SetGameVersion(string version) =>
        Modules.Setup(m => m.ReadModule(It.IsAny<string>()))
            .Returns(() => TestHelpers.GenerateMockVersionID(("VERSION_TEXT", version), ("DEBUG_INFO", "BUILD_INFO")).module);

    private CliContext Build()
    {
        ServiceCollection services = new();
        services.AddSingleton<IFileSystem>(FileSystem);
        services.AddSingleton<IZipFileService, ZipFileService>();
        services.AddSingleton<IInstallationStateService, InstallationStateService>();
        services.AddSingleton<IEnvironmentProvider>(Environment);
        services.AddSingleton(Mock.Of<IEnvironmentVariableProvider>());
        services.AddSingleton(Mock.Of<ILogService>());
        services.AddSingleton(Mock.Of<IMessageBoxService>());
        services.AddSingleton<ILauncherConfigService, LauncherConfigService>();
        services.AddSingleton(Modules.Object);
        services.AddSingleton<IKsp2InstallService, Ksp2InstallService>();
        services.AddSingleton<IKsp2GameUninstallService, Ksp2GameUninstallService>();
        services.AddSingleton<IReleasesFeedService, ReleasesFeedService>();
        services.AddSingleton(InstallPlan.Object);
        services.AddSingleton(Mock.Of<IKsp2DetectorService>());
        services.AddSingleton(Mock.Of<IAssemblyService>());
        services.AddSingleton(GameDataFolder.Object);
        services.AddSingleton(News.Object);
        services.AddSingleton(FeedProvider.Object);
        services.AddSingleton(OperatingSystem.Object);
        services.AddSingleton(DiskSpace.Object);
        services.AddSingleton(Wine.Object);
        services.AddSingleton(Steam.Object);
        services.AddSingleton(Downloader.Object);

        return new CliContext(services.BuildServiceProvider(), Output);
    }
}
