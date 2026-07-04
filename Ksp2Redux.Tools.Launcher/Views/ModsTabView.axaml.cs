using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ksp2Redux.Tools.Launcher.Views;

public partial class ModsTabView : UserControl
{
    public Border? GlassPanelBorder => this.FindControl<Border>("GlassPanel");

    public ModsTabView() => AvaloniaXamlLoader.Load(this);
}
