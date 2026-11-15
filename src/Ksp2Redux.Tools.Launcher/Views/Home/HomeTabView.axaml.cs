using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ksp2Redux.Tools.Launcher.Localization;
using Ksp2Redux.Tools.Launcher.Services.Infrastructure;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;

namespace Ksp2Redux.Tools.Launcher.Views.Home;

public partial class HomeTabView : UserControl
{
    // How close to the end counts as following the log; a reader scrolled further up is left alone.
    private const double FOLLOW_SLACK = 24;

    private HomeTabViewModel? _model;
    private readonly ListBox _logList;
    private ScrollViewer? _logScroller;
    private bool _scrollQueued;

    private HomeTabViewModel? Model => DataContext as HomeTabViewModel;
    public Border? GlassPanelBorder => this.FindControl<Border>("GlassPanel");

    public HomeTabView()
    {
        AvaloniaXamlLoader.Load(this);
        _logList = this.FindControl<ListBox>("InstallLogList")!;
        DataContextChanged += (_, _) => Attach(Model);
    }

    private async void TriggerInstall(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.UpdateLauncher();
    }

    private void Attach(HomeTabViewModel? model)
    {
        if (_model is not null) _model.InstallLogEntries.CollectionChanged -= OnLogChanged;
        _model = model;
        if (_model is not null) _model.InstallLogEntries.CollectionChanged += OnLogChanged;
    }

    // A flush adds its lines one at a time, so the scroll is queued once for the whole batch.
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollQueued) return;
        _logScroller ??= _logList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_logScroller is not { } scroller) return;
        if (scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - FOLLOW_SLACK) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            scroller.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private async void InstallLogList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+C, or Cmd+C on macOS.
        var isCopy = e.Key == Key.C && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (!isCopy || _model is null || _logList.Selection.SelectedIndexes is not { Count: > 0 } selected) return;
        e.Handled = true;
        var entries = _model.InstallLogEntries;
        var lines = selected.Order().Select(index => entries[index]);
        await CopyToClipboard(string.Join('\n', lines));
    }

    private async void CopyInstallLog_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        await CopyToClipboard(string.Join('\n', _model.InstallLogEntries));
    }

    private async Task CopyToClipboard(string text)
    {
        if (TopLevel.GetTopLevel(this) is not { Clipboard: { } clipboard } topLevel) return;
        try
        {
            await clipboard.SetTextAsync(text);
            (topLevel as MainWindow)?.ShowSnackbar(TExtension.Service["Home.InstallLog.Copied"]);
        }
        catch (Exception ex)
        {
            AvaloniaLogSink.Target?.Error("Could not copy the install log to the clipboard.", ex);
            (topLevel as MainWindow)?.ShowSnackbar(TExtension.Service["Clipboard.CopyFailed"]);
        }
    }
}
