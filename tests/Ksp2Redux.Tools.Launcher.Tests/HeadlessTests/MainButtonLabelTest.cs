using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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

public class MainButtonLabelTest
{
    // The artwork of every main button has its icon baked in from x=178 of the 260 px canvas.
    private const double ICON_LEFT = 178;
    private const double LABEL_LEFT = 24;

    private static readonly (string Button, string Key, string English, string Long)[] BUTTONS =
    [
        ("InstallButton", "Sidebar.InstallButton", "INSTALL", "INSTALLATIONSPROGRAMM"),
        ("UpdateButton", "Sidebar.UpdateButton", "UPDATE", "AKTUALISIERUNGSDIENST"),
        ("CancelButton", "Sidebar.CancelButton", "CANCEL", "ABBRECHENUNDZURUECK"),
        ("LaunchButton", "Sidebar.LaunchButton", "LAUNCH", "SPIELSTARTVORGANG")
    ];

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

    // Goes through the compiled XAML of the real window, since the label box is a layout rule.
    [AvaloniaTest]
    public void LongLabels_ShrinkToStayLeftOfTheIcon_WhileEnglishKeepsItsSize()
    {
        var source = new FakeStringSource();
        foreach (var (_, key, english, longText) in BUTTONS)
        {
            source.Add("en", key, english).Add("de", key, longText);
        }
        var service = new LocalizationService(source, CultureInfo.GetCultureInfo("en-US"));
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
        var buttons = BUTTONS.ToDictionary(b => b.Button,
            b => window.GetVisualDescendants().OfType<Button>().First(button => button.Name == b.Button));

        double LabelRightEdge(string name, out double unscaledWidth)
        {
            foreach (var (other, button) in buttons)
            {
                button.IsVisible = other == name;
            }
            Pump();
            var canvas = buttons[name].GetVisualDescendants().OfType<Canvas>().First();
            var label = canvas.GetVisualDescendants().OfType<TextBlock>().First();
            unscaledWidth = label.Bounds.Width;
            return label.TranslatePoint(new Point(label.Bounds.Width, 0), canvas)!.Value.X;
        }

        foreach (var (name, _, _, _) in BUTTONS)
        {
            var englishRight = LabelRightEdge(name, out var englishWidth);
            Assert.That(englishRight, Is.EqualTo(LABEL_LEFT + englishWidth).Within(0.5), $"{name}: English must not be scaled");
        }

        service.SetLanguage("de");
        Pump();
        foreach (var (name, _, _, _) in BUTTONS)
        {
            var longRight = LabelRightEdge(name, out var longWidth);
            Assert.That(longWidth + LABEL_LEFT, Is.GreaterThan(ICON_LEFT), $"{name}: the test text must be longer than the space");
            Assert.That(longRight, Is.LessThanOrEqualTo(ICON_LEFT), $"{name}: the label runs into the icon");
        }
    }
}
