using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;
using System.ComponentModel;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class HomeTabView : UserControl
{
    private HomeTabViewModel? Model => DataContext as HomeTabViewModel;
    private Button? LaunchButtonControl => this.FindControl<Button>("LaunchButton");
    private Button? UpdateButtonControl => this.FindControl<Button>("UpdateButton");
    private Button? CancelButtonControl => this.FindControl<Button>("CancelButton");
    private Button? InstallButtonControl => this.FindControl<Button>("InstallButton");
    private TextBox? InstallLogTextBoxControl => this.FindControl<TextBox>("InstallLogTextBox");

    public HomeTabView()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += RefreshAll;
    }

    private async void RefreshAll(object? sender, RoutedEventArgs e)
    {
        if (Model is not null)
        {
            // await Model.UpdateVersionsList();
            ShowButton(Model.MainButtonShown);
            Model.PropertyChanged += ReactToHomeTabPropertyChanged;
        }
    }

    private void ReactToHomeTabPropertyChanged(object? sender, PropertyChangedEventArgs? e)
    {
        if (Model is not null)
        {
            ShowButton(Model.MainButtonShown);
        }
    }

    private void ShowButton(HomeTabViewModel.MainButtonState which)
    {
        if (LaunchButtonControl is not { } launchButton ||
            UpdateButtonControl is not { } updateButton ||
            CancelButtonControl is not { } cancelButton ||
            InstallButtonControl is not { } installButton)
        {
            return;
        }

        launchButton.IsVisible = false;
        updateButton.IsVisible = false;
        cancelButton.IsVisible = false;
        installButton.IsVisible = false;
        switch (which)
        {
            case HomeTabViewModel.MainButtonState.Launch:
                launchButton.IsVisible = true;
                break;
            case HomeTabViewModel.MainButtonState.Install:
                installButton.IsVisible = true;
                break;
            case HomeTabViewModel.MainButtonState.Update:
                updateButton.IsVisible = true;
                break;
            case HomeTabViewModel.MainButtonState.Cancel:
                cancelButton.IsVisible = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(which), which, null);
        }
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
