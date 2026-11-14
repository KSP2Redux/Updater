using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;
using TheArtOfDev.HtmlRenderer.Avalonia;
using TheArtOfDev.HtmlRenderer.Core.Entities;

namespace Ksp2Redux.Tools.Launcher.Views.Community;

public partial class CommunityTabView : UserControl
{
    public CommunityTabViewModel ViewModel => (CommunityTabViewModel)DataContext!;
    public Border? GlassPanelBorder => this.FindControl<Border>("GlassPanel");

    public CommunityTabView() => AvaloniaXamlLoader.Load(this);

    private async void NewsLink_OnClick(object? sender, RoutedEventArgs e)
    {
        await ViewModel.LaunchExternalLinkAsync(TopLevel.GetTopLevel(this), ViewModel.SelectedNews.Link);
    }

    private async void Html_OnLinkClicked(object? sender, RoutedEventArgs e)
    {
        if (e is not HtmlRendererRoutedEventArgs<HtmlLinkClickedEventArgs> { Event: var clicked })
        {
            return;
        }

        clicked.Handled = true;
        var link = Uri.TryCreate(ViewModel.SelectedNews.Link, UriKind.Absolute, out var post) && Uri.TryCreate(post, clicked.Link, out var resolved)
            ? resolved.AbsoluteUri
            : clicked.Link;
        await ViewModel.LaunchExternalLinkAsync(TopLevel.GetTopLevel(this), link);
    }
}
