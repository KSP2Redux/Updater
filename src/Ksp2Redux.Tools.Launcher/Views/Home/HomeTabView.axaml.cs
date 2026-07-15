using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;

namespace Ksp2Redux.Tools.Launcher.Views.Home;

public partial class HomeTabView : UserControl
{
    private HomeTabViewModel? Model => DataContext as HomeTabViewModel;
    private TextBox? InstallLogTextBoxControl => this.FindControl<TextBox>("InstallLogTextBox");
    public Border? GlassPanelBorder => this.FindControl<Border>("GlassPanel");

    public HomeTabView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void TriggerInstall(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;
        await Model.UpdateLauncher();
    }

    private void InstallLogTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (InstallLogTextBoxControl is not { } installLogTextBox) return;
        var lastLineIndex = Math.Max(0, installLogTextBox.GetLineCount() - 1);
        installLogTextBox.ScrollToLine(lastLineIndex);
    }
}
