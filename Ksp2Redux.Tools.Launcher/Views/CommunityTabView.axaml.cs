using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class CommunityTabView : UserControl
{
    public CommunityTabViewModel ViewModel => (CommunityTabViewModel)DataContext!;
    public Border? GlassPanelBorder => this.FindControl<Border>("GlassPanel");

    public CommunityTabView() => AvaloniaXamlLoader.Load(this);

    private void LaunchUri(Uri uri)
    {
        TopLevel.GetTopLevel(this)!.Launcher.LaunchUriAsync(uri);
    }

    private void NewsLink_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedNews.Link is null)
            return;
        
        LaunchUri(new Uri(ViewModel.SelectedNews.Link));
    }
}
