using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Platform;
using Ksp2Redux.Tools.Launcher.Views.Community;
using TheArtOfDev.HtmlRenderer.Avalonia;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class NewsHtmlCultureTest
{
    [AvaloniaTest]
    [TestCase("th-TH")]
    [TestCase("en-US")]
    [TestCase("fr-FR")]
    public void CommunityNews_StartupStylesheet_PreservesLinkColorAndCulture(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            CultureInfo.CurrentCulture = culture;
            var view = new CommunityTabView();
            var panel = view.FindControl<HtmlPanel>("Html")!;
            using var stream = AssetLoader.Open(new Uri("avares://Ksp2Redux.Tools.Launcher/Assets/news.css"));
            using var reader = new StreamReader(stream);

            panel.BaseStylesheet = reader.ReadToEnd();
            panel.Text = "<a href='https://ksp2redux.org'>News link</a>";

            Assert.That(panel.GetHtml().Contains("color: #DC1501", StringComparison.OrdinalIgnoreCase), Is.True);
            Assert.That(CultureInfo.CurrentCulture, Is.SameAs(culture));
            Assert.That(CultureInfo.CurrentUICulture, Is.SameAs(originalUiCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [AvaloniaTest]
    public void CommunityNews_LaterContentUpdate_PreservesEmbeddedCssAndCulture()
    {
        var view = new CommunityTabView();
        var panel = view.FindControl<HtmlPanel>("Html")!;
        var originalCulture = CultureInfo.CurrentCulture;
        var culture = CultureInfo.GetCultureInfo("th-TH");
        try
        {
            CultureInfo.CurrentCulture = culture;
            panel.Text = "<style>/* News styling */ p { color:#123456; }</style><p>First article</p>";
            Assert.That(panel.GetHtml().Contains("color: #123456", StringComparison.OrdinalIgnoreCase), Is.True);

            panel.Text = "<style>/* Updated styling */ p { color:#654321; }</style><p>UpdatedArticle</p>";
            Assert.That(panel.GetHtml().Contains("color: #654321", StringComparison.OrdinalIgnoreCase), Is.True);
            Assert.That(panel.GetHtml(), Does.Contain("UpdatedArticle"));
            Assert.That(CultureInfo.CurrentCulture, Is.SameAs(culture));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
