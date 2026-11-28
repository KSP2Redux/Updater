using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Tests.Localization;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class LocalizedBindingTest
{
    private CultureInfo _uiCulture = null!;
    private CultureInfo? _defaultUiCulture;

    [SetUp]
    public void RememberCultures()
    {
        _uiCulture = CultureInfo.CurrentUICulture;
        _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
    }

    [TearDown]
    public void Restore()
    {
        TExtension.Service = LocalizationService.Default;
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }

    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    public void Markup_BindsTheDottedKeyAndRefreshesWhenTheLanguageChanges()
    {
        var service = new LocalizationService(
            new FakeStringSource()
                .Add("en", "Settings.Profile.Add.Tooltip", "Add a profile")
                .Add("de", "Settings.Profile.Add.Tooltip", "de:Add a profile"),
            CultureInfo.GetCultureInfo("en-US"));
        TExtension.Service = service;

        var binding = new TExtension("Settings.Profile.Add.Tooltip").ProvideValue(Mock.Of<IServiceProvider>());
        var block = new TextBlock();
        block.Bind(TextBlock.TextProperty, (BindingBase)binding);

        Assert.That(block.Text, Is.EqualTo("Add a profile"));

        service.SetLanguage("de");
        Dispatcher.UIThread.RunJobs();

        Assert.That(block.Text, Is.EqualTo("de:Add a profile"));
    }

    // Goes through the compiled XAML of the real window, so it covers what the manual binding above cannot.
    [AvaloniaTest]
    public void MainWindow_TabHeaderAndLaunchButtonFollowTheLanguage()
    {
        var service = new LocalizationService(
            new FakeStringSource()
                .Add("en", "Tab.Home", "Home").Add("de", "Tab.Home", "de:Home")
                .Add("en", "Sidebar.LaunchButton", "LAUNCH").Add("de", "Sidebar.LaunchButton", "de:LAUNCH"),
            CultureInfo.GetCultureInfo("en-US"));
        TExtension.Service = service;
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();

        var window = new MainWindow
        {
            DataContext = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>(),
            Width = 1280,
            Height = 800
        };
        window.Show();
        Pump();
        var homeTab = window.FindControl<TabItem>("HomeTab")!;
        string LaunchText() => window.GetVisualDescendants().OfType<TextBlock>()
            .Single(block => block.Text is "LAUNCH" or "de:LAUNCH").Text!;

        Assert.That(homeTab.Header, Is.EqualTo("Home"));
        Assert.That(LaunchText(), Is.EqualTo("LAUNCH"));

        service.SetLanguage("de");
        Pump();

        Assert.That(homeTab.Header, Is.EqualTo("de:Home"));
        Assert.That(LaunchText(), Is.EqualTo("de:LAUNCH"));
    }
}
