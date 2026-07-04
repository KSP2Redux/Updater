using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ksp2Redux.Tools.Launcher.Views.Shared;

public partial class NewsCollectionView : UserControl
{
    public NewsCollectionView() => AvaloniaXamlLoader.Load(this);

    public Border? NewsPanelBorder => this.FindControl<Border>("NewsPanel");
}
