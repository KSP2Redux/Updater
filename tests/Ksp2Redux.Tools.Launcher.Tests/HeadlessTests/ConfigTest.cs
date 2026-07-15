using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Common.Models;
using Ksp2Redux.Tools.Launcher.Controls;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class ConfigTest
{
    [AvaloniaTest]
    public void Config_WithInstall_InstallInComboBox()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.EnvironmentProvider.SetFolderPath(Environment.SpecialFolder.LocalApplicationData, "AppDataLocal");
        TestAppBuilder.FileSystem.Directory.CreateDirectory("AppDataLocal/Ksp2Redux");
        string configFileContent = """
                            {
                                "Ksp2InstallPath": "",
                                "ReleaseChannel": null,
                                "LaunchThroughSteam": false,
                                "SteamAppId": "954850",
                                "LaunchArguments": "-popupwindow",
                                "LastInstalledVersion": null,
                                "Ksp2Installs": [
                                    {
                                        "Id": "11111111-1111-1111-1111-111111111111",
                                        "Name": "Kerbal Space Program 2",
                                        "ExePath": "KSP2_x64.exe",
                                        "ReleaseChannel": "beta",
                                        "LastInstalledVersion": null,
                                        "LaunchThroughSteam": false,
                                        "SteamAppId": "954850",
                                        "LaunchArguments": "-popupwindow",
                                        "DisableGraphicsJobs": false
                                    }
                                ],
                                "ActiveKsp2InstallId": "11111111-1111-1111-1111-111111111111",
                                "Feeds": [],
                                "LauncherRepo": "https://launcher_repo.com"
                            }
                            """;
        TestAppBuilder.FileSystem.File.WriteAllText("AppDataLocal/Ksp2Redux/redux-launcher-config.json", configFileContent);
        
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));

        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed()
        {
            Items = []
        });

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
        
        // Act
        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        
        // Assert
        Ksp2InstallChoiceViewModel expectedSelectedItem = new(new()
        {
            ExePath = "KSP2_x64.exe",
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Kerbal Space Program 2",
        });
        
        ComboBox? combobox = window
            .GetVisualDescendants()
            .OfType<ComboBox>()
            .FirstOrDefault(x => x.Name == "InstallSelector");
        Assert.That(combobox, Is.Not.Null);
        Assert.That(combobox.SelectedItem, Is.TypeOf<Ksp2InstallChoiceViewModel>());
        Assert.That((Ksp2InstallChoiceViewModel)combobox.SelectedItem, Is.EqualTo(expectedSelectedItem).UsingPropertiesComparer());
        Assert.That(combobox.Items, Is.Not.Null);
        Assert.That(combobox.Items, Is.Not.Empty);
        Assert.That(combobox.Items, Has.Count.EqualTo(1));
    }
    
    [AvaloniaTest]
    public void Config_WithFeed_PatchesInComboBox()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestAppBuilder.EnvironmentProvider.SetFolderPath(Environment.SpecialFolder.LocalApplicationData, "AppDataLocal");
        TestAppBuilder.FileSystem.Directory.CreateDirectory("AppDataLocal/Ksp2Redux");
        string configFileContent = """
                                  {
                                      "Ksp2InstallPath": "",
                                      "ReleaseChannel": null,
                                      "LaunchThroughSteam": false,
                                      "SteamAppId": "954850",
                                      "LaunchArguments": "-popupwindow",
                                      "LastInstalledVersion": null,
                                      "Ksp2Installs": [
                                          {
                                              "Id": "11111111-1111-1111-1111-111111111111",
                                              "Name": "Kerbal Space Program 2",
                                              "ExePath": "KSP2_x64.exe",
                                              "ReleaseChannel": "channel-1",
                                              "LastInstalledVersion": null,
                                              "LaunchThroughSteam": false,
                                              "SteamAppId": "954850",
                                              "LaunchArguments": "-popupwindow",
                                              "DisableGraphicsJobs": false
                                          }
                                      ],
                                      "ActiveKsp2InstallId": "11111111-1111-1111-1111-111111111111",
                                      "Feeds": [
                                          {
                                              "Repository": "https://feed-repo.com",
                                              "Filename": "manifest.json"
                                          }
                                      ],
                                      "LauncherRepo": "https://launcher_repo.com"
                                  }
                                  """;
        TestAppBuilder.FileSystem.File.WriteAllText("AppDataLocal/Ksp2Redux/redux-launcher-config.json", configFileContent);
        
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));

        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed()
        {
            Items = []
        });
        
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

        ReleasePatch patch0 = new()
        {
            ReleasedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Version = "1.1.1.0.0",
            ChecksumSha256 = "0",
            Url = "https://example.com/patch0.patch",
            Size = 0,
            Requires = new PatchRequirement(),
        };
        ReleasePatch patch1 = new()
        {
            ReleasedAt = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Version = "1.1.2.0.1",
            ChecksumSha256 = "1",
            Url = "https://example.com/patch1.patch",
            Size = 0,
            Requires = new PatchRequirement(),
        };

        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.Is<FeedInfo>(f => f.Filename == "manifest.json")))
            .ReturnsAsync(new ReleaseManifest
            {
                Channel = "channel-1",
                GeneratedAt = new DateTime(2020, 1, 4, 0, 0, 0, DateTimeKind.Utc),
                Patches = [patch0, patch1],
                SchemaVersion = 1
            });
        
        // Act
        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();

        // Assert
        GameVersionViewModel expectedSelectedItem = new(new()
        {
            BuildNumber = "1",
            VersionNumber = new Version(1, 1, 2, 0),
            Channel = "channel-1",
            CommitHash = "1",
            ReleasedAt = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        });
        List<GameVersionViewModel> expectedItems =
        [
            expectedSelectedItem,
            new(new()
            {
                BuildNumber = "0",
                VersionNumber = new Version(1, 1, 1, 0),
                Channel = "channel-1",
                CommitHash = "0",
                ReleasedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            }),
        ];
        
        GroupedComboBox? combobox = window
            .GetVisualDescendants()
            .OfType<GroupedComboBox>()
            .FirstOrDefault(x => x.Name == "VersionSelector");
        Assert.That(combobox, Is.Not.Null);
        
        Assert.That(combobox.SelectedItem, Is.TypeOf<GameVersionViewModel>());
        Assert.That((GameVersionViewModel)combobox.SelectedItem, Is.EqualTo(expectedSelectedItem).UsingPropertiesComparer());
        
        Assert.That(combobox.GroupedItems, Is.Not.Null);
        Assert.That(combobox.GroupedItems, Is.Not.Empty);
        Assert.That(combobox.GroupedItems.OfType<GameVersionViewModel>(), Is.EquivalentTo(expectedItems).UsingPropertiesComparer());
    }

    [AvaloniaTest]
    public void Config_NoConfigSteamGameStock_DetectsGameCreateConfig()
    {
        // Arrange
        TestAppBuilder.OperatingSystemService.Setup(o => o.IsLinux()).Returns(false);
        TestHelpers.MockKsp2StockSteamInstall();
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed{ Items = [] });
        TestHelpers.MockMessageBoxAcceptAll();
        
        TestAppBuilder.ManifestReleasesFeedProviderService
            .Setup(m => m.GetManifest(It.IsAny<FeedInfo>()))
            .ReturnsAsync((FeedInfo f) => new ReleaseManifest
            {
                Channel = f.Filename.Split('-', '.')[1],
                GeneratedAt = new DateTime(2020, 1, 4, 0, 0, 0, DateTimeKind.Utc),
                Patches = [],
                SchemaVersion = 1
            });
        
        // Act
        MainWindow window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
        };
        window.Show();
        
        // Assert
        GameVersionViewModel expectedSelectedItem = new(new()
        {
            BuildNumber = "32914",
            VersionNumber = new Version(0, 2, 2, 0),
            Channel = "stable",
            CommitHash = null
        });
        
        TestAppBuilder.MessageBoxService.Verify(m => m.ShowMessageBoxAsOwnedAsync(
            "KSP2 Install Found", 
            It.IsAny<string>(),
            ButtonEnum.YesNo,
            It.IsAny<Icon>(),
            It.IsAny<object>(),
            WindowStartupLocation.CenterOwner));
        
        GroupedComboBox? combobox = window
            .GetVisualDescendants()
            .OfType<GroupedComboBox>()
            .FirstOrDefault(x => x.Name == "VersionSelector");
        Assert.That(combobox, Is.Not.Null);
        
        Assert.That(combobox.SelectedItem, Is.TypeOf<GameVersionViewModel>());
        Assert.That((GameVersionViewModel)combobox.SelectedItem, Is.EqualTo(expectedSelectedItem).UsingPropertiesComparer());

        string expectedLauncherConfigName = """
                  "Name": "Kerbal Space Program 2",
            """;
        
        string expectedLauncherConfigExePath = """
                  "ExePath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Kerbal Space Program 2\\KSP2_x64.exe",
            """;
        
        Assert.That(TestAppBuilder.FileSystem.File.Exists(@"AppDataLocal\Ksp2Redux\redux-launcher-config.json"), Is.True);
        Assert.That(TestAppBuilder.FileSystem.File.ReadAllText(@"AppDataLocal\Ksp2Redux\redux-launcher-config.json"),
            Contains.Substring(expectedLauncherConfigName));
        Assert.That(TestAppBuilder.FileSystem.File.ReadAllText(@"AppDataLocal\Ksp2Redux\redux-launcher-config.json"),
            Contains.Substring(expectedLauncherConfigExePath));
    }
}
