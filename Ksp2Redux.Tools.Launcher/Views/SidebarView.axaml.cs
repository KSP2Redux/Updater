using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels;
using Ksp2Redux.Tools.Launcher.ViewModels.Home;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class SidebarView : UserControl
{
    private HomeTabViewModel? Model => (DataContext as MainWindowViewModel)?.HomeTab;
    private Button? LaunchButtonControl => this.FindControl<Button>("LaunchButton");
    private Button? UpdateButtonControl => this.FindControl<Button>("UpdateButton");
    private Button? CancelButtonControl => this.FindControl<Button>("CancelButton");
    private Button? InstallButtonControl => this.FindControl<Button>("InstallButton");
    private Shared.NewsCollectionView? NewsCollectionControl => this.FindControl<Shared.NewsCollectionView>("NewsCollection");

    public Border? NewsPanelBorder => NewsCollectionControl?.NewsPanelBorder;

    public SidebarView()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += RefreshAll;
    }

    private void RefreshAll(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        ShowButton(model.MainButtonShown);
        model.PropertyChanged += ReactToHomeTabPropertyChanged;
    }

    private void ReactToHomeTabPropertyChanged(object? sender, PropertyChangedEventArgs? e)
    {
        if (Model is not { } model) return;
        ShowButton(model.MainButtonShown);
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

    private void LaunchUri(Uri uri)
    {
        TopLevel.GetTopLevel(this)!.Launcher.LaunchUriAsync(uri);
    }

    private void DiscordLink_OnClick(object? sender, RoutedEventArgs e)
    {
        LaunchUri(new Uri("https://discord.gg/8yq8d5VGQR"));
    }

    private void ForumsLink_OnClick(object? sender, RoutedEventArgs e)
    {
        LaunchUri(new Uri("https://forum.kerbalspaceprogram.com/topic/226985-ksp2-redux"));
    }

    private void WikiLink_OnClick(object? sender, RoutedEventArgs e)
    {
        LaunchUri(new Uri("https://kerbal.wiki"));
    }

    private void YoutubeLink_OnClick(object? sender, RoutedEventArgs e)
    {
        LaunchUri(new Uri("https://www.youtube.com/@RendezvousEntertainmentModding"));
    }

    private void GithubLink_OnClick(object? sender, RoutedEventArgs e)
    {
        LaunchUri(new Uri("https://github.com/KSP2Redux"));
    }
}
