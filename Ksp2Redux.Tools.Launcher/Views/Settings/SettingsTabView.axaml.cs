using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;

namespace Ksp2Redux.Tools.Launcher.Views.Settings;

public partial class SettingsTabView : UserControl
{
    private SettingsTabViewModel Model => (DataContext as SettingsTabViewModel)!;
    public Border? GlassPanelBorder => this.FindControl<Border>("GlassPanel");

    public SettingsTabView() => AvaloniaXamlLoader.Load(this);

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
