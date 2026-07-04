using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Settings;

namespace Ksp2Redux.Tools.Launcher.Views;

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
}
