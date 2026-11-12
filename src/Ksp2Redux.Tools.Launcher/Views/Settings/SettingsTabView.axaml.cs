using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;

namespace Ksp2Redux.Tools.Launcher.Views.Settings;

public partial class SettingsTabView : UserControl
{
    private SettingsTabViewModel Model => (DataContext as SettingsTabViewModel)!;
    public Border? GlassPanelBorder => this.FindControl<Border>("GlassPanel");

    private SettingsTabViewModel? _observedModel;

    public SettingsTabView()
    {
        AvaloniaXamlLoader.Load(this);
        Focusable = true;

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_observedModel is not null) _observedModel.PropertyChanged -= OnModelPropertyChanged;
        _observedModel = DataContext as SettingsTabViewModel;
        if (_observedModel is not null) _observedModel.PropertyChanged += OnModelPropertyChanged;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsTabViewModel.IsRenamingInstall) || !Model.IsRenamingInstall) return;

        // Posted because the box is not focusable until its visibility binding updates, after this handler.
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<TextBox>("RenameBox") is not { } box) return;
            box.Focus();
            box.SelectAll();
        });
    }

    // Escape never arrives: window key bindings run first, and MainWindowViewModel.HandleEscape cancels the rename.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is not TextBox { AcceptsReturn: false } box) return;

        if (box.Name == "RenameBox") Model.CommitRenameInstall();
        else ClearFocus();
        e.Handled = true;
    }

    private void OnPointerPressedAnywhere(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;
        ClearFocus();
    }

    // Avalonia has no way to drop focus altogether, so the view takes it. By pointer, so no focus ring is drawn.
    private void ClearFocus() => Focus(NavigationMethod.Pointer);

    private void RenameBox_LostFocus(object? sender, RoutedEventArgs e) => Model.CommitRenameInstall();

    private async void UninstallReduxClick(object? sender, RoutedEventArgs e)
    {
        await Model.UninstallRedux();
    }

    private async void InstallFromPatchFile(object? sender, RoutedEventArgs e)
    {
        await Model.InstallFromPatchFile();
    }

    private async void OpenLogsFolderClick(object? sender, RoutedEventArgs e)
    {
        await Model.OpenLogsFolder();
    }

    private async void CopyDiagnosticInfoClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(Model.BuildDiagnosticInfo());
    }
}
