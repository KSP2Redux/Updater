using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ksp2Redux.Tools.Launcher.Models;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class CommunityTabView : UserControl
{
    public CommunityTabViewModel ViewModel => (CommunityTabViewModel)DataContext!;

    public CommunityTabView()
    {
        InitializeComponent();
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