using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.NUnit;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class MessageBoxLanguageFontsTest
{
    [AvaloniaTest]
    [TestCase("ja", "Noto Sans JP", "確認、続行します。\n次の行も確認します。", false)]
    [TestCase("zh-Hans", "Noto Sans SC", "确认、是否继续。\n下一行也需要确认。", true)]
    [TestCase("zh-Hant", "Noto Sans TC", "確認、是否繼續。\n下一行也需要確認。", false)]
    public void DialogBodyShapesPunctuationWithTheSelectedLanguagesFont(
        string cultureName, string fontName, string text, bool scrollable)
    {
        var localization = Mock.Of<ILocalizationService>(service => service.Culture == CultureInfo.GetCultureInfo(cultureName));
        using var services = new ServiceCollection().AddSingleton(localization).BuildServiceProvider();
        var service = ActivatorUtilities.CreateInstance<MessageBoxService>(services);
        const string title = "KSP2 Redux";
        var parameters = scrollable
            ? MessageBoxService.ScrollableParams(title, text, 400, ButtonEnum.OkCancel, WindowStartupLocation.CenterScreen)
            : new MessageBoxStandardParams { ContentTitle = title, ContentMessage = text };

        Window? opened = null;
        using var subscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => opened = window);
        try
        {
            _ = service.CreateMessageBox(parameters).ShowWindowAsync();
            for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

            Assert.That(opened, Is.Not.Null, "The message box never opened.");
            var body = opened!.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ContentTextBox");
            var presenter = body.GetVisualDescendants().OfType<TextPresenter>().Single();
            var punctuationRuns = presenter.TextLayout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>()
                .Where(run => run.Text.Span.Contains('\u3001') || run.Text.Span.Contains('\u3002')).ToArray();

            Assert.That(string.Concat(punctuationRuns.Select(run => run.Text.ToString())), Does.Contain("、").And.Contain("。"));
            Assert.That(punctuationRuns.Select(run => run.GlyphRun.GlyphTypeface.FamilyName), Is.All.EqualTo(fontName));
        }
        finally
        {
            opened?.Close();
        }
    }
}
