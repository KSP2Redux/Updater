using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class CommunityTabView : UserControl
{
    private readonly ILauncher _launcher;

    public CommunityTabView()
    {
        InitializeComponent();
        _launcher = TopLevel.GetTopLevel(this)!.Launcher;
    }

    private void DiscordLink_OnClick(object? sender, RoutedEventArgs e)
    {
        _launcher.LaunchUriAsync(new Uri("https://discord.gg/8yq8d5VGQR"));
    }

    private void ForumsLink_OnClick(object? sender, RoutedEventArgs e)
    {
        _launcher.LaunchUriAsync(new Uri("https://forum.kerbalspaceprogram.com/topic/226985-ksp2-redux"));
    }

    private void WikiLink_OnClick(object? sender, RoutedEventArgs e)
    {
        _launcher.LaunchUriAsync(new Uri("https://kerbal.wiki"));
    }

    private void YoutubeLink_OnClick(object? sender, RoutedEventArgs e)
    {
        _launcher.LaunchUriAsync(new Uri("https://www.youtube.com/@RendezvousEntertainmentModding"));
    }

    private void GithubLink_OnClick(object? sender, RoutedEventArgs e)
    {
        _launcher.LaunchUriAsync(new Uri("https://github.com/KSP2Redux"));
    }
}