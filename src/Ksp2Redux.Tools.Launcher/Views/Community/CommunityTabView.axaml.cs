using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Ksp2Redux.Tools.Launcher.ViewModels.Community;

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
}
