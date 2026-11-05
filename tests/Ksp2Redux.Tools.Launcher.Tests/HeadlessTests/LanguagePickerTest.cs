using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeHollow.FeedReader;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.Views;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class LanguagePickerTest
{
    private CultureInfo _uiCulture = null!;
    private CultureInfo? _defaultUiCulture;

    [SetUp]
    public void RememberCultures()
    {
        _uiCulture = CultureInfo.CurrentUICulture;
        _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
    }

    // The app's markup and the container share LocalizationService.Default, so the language picked here
    // is put back for the tests that follow.
    [TearDown]
    public void Restore()
    {
        LocalizationService.Default.SetLanguage("en");
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

    // The closed picker shows SelectionBoxItem. A pick switches the language, which rebuilt the list while the
    // picker was still handling the pick, and left the language selected but the closed picker blank.
    [AvaloniaTest]
    public void PickingALanguage_ShowsItInTheClosedPicker()
    {
        // Arrange
        TestAppBuilder.UpdateService.Setup(u => u.CheckAndPerformUpdateAsync()).Returns(Task.FromResult(true));
        TestAppBuilder.NewsProviderService.Setup(n => n.GetSyndicationFeed()).ReturnsAsync(new Feed { Items = [] });
        TestHelpers.MockKsp2StockSteamInstall();
        TestHelpers.MockMessageBoxAcceptAll();

        var model = TestAppBuilder.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = model };
        window.Show();

        // The settings tab is only built once it is the current one.
        model.CurrentTab = MainWindowViewModel.SettingsTabId;
        Pump();

        var picker = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => ReferenceEquals(c.ItemsSource, model.SettingsTab.LanguageChoices));

        foreach (var cultureName in new[] { "ja", "en", "" })
        {
            var choice = model.SettingsTab.LanguageChoices.First(language => language.CultureName == cultureName);

            // Act: what a click on the entry in the open list does.
            picker.SelectedItem = choice;
            Pump();

            // Assert
            Assert.That((picker.SelectionBoxItem as LanguageOption)?.CultureName, Is.EqualTo(cultureName),
                $"the closed picker after picking '{choice.DisplayName}'");
        }
    }
}
