using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ksp2Redux.Tools.Launcher.Services.Feeds;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace Ksp2Redux.Tools.Launcher.Tests.HeadlessTests;

public class UpdateFoundDialogTest
{
    private sealed record DialogLayout(double WindowHeight, double NotesExtent, double NotesViewport, bool OkVisible);

    private static DialogLayout Show(string text, double maxHeight)
    {
        Window? opened = null;
        using var subscription = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => opened = window);

        _ = MessageBoxManager.GetMessageBoxStandard(MessageBoxService.ScrollableParams(
                "Update Found", text, maxHeight, ButtonEnum.OkCancel, WindowStartupLocation.CenterScreen))
            .ShowWindowAsync();
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

        Assert.That(opened, Is.Not.Null, "The update dialog never opened.");
        var notes = opened!.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ContentTextBox")
            .GetVisualDescendants().OfType<ScrollViewer>().First();
        var ok = opened.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OkButton");
        var layout = new DialogLayout(opened.Bounds.Height, notes.Extent.Height, notes.Viewport.Height, ok.IsEffectivelyVisible);
        opened.Close();
        return layout;
    }

    private static string Notes(int lines) =>
        string.Join("\n", Enumerable.Range(1, lines).Select(i => $"- Change number {i}, with enough words to wrap like a real line"));

    [AvaloniaTest]
    public void LongReleaseNotes_KeepTheDialogAtItsUsualSizeAndScroll()
    {
        var layout = Show(UpdateService.BuildUpdateFoundMessage(new Version(0, 6, 1), Notes(80)),
            UpdateService.UPDATE_DIALOG_MSBOX_MAX_HEIGHT);

        Assert.Multiple(() =>
        {
            Assert.That(layout.WindowHeight, Is.InRange(380, 430));
            Assert.That(layout.NotesExtent, Is.GreaterThan(layout.NotesViewport), "The notes should scroll.");
            Assert.That(layout.OkVisible, Is.True);
        });
    }

    [AvaloniaTest]
    public void ShortReleaseNotes_AreNotAffectedByTheCap()
    {
        var text = UpdateService.BuildUpdateFoundMessage(new Version(0, 6, 1), Notes(3));

        var capped = Show(text, UpdateService.UPDATE_DIALOG_MSBOX_MAX_HEIGHT);
        var uncapped = Show(text, double.PositiveInfinity);

        Assert.Multiple(() =>
        {
            Assert.That(capped.WindowHeight, Is.EqualTo(uncapped.WindowHeight));
            Assert.That(capped.NotesExtent, Is.EqualTo(capped.NotesViewport), "Short notes should not scroll.");
        });
    }
}
