using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Ksp2Redux.Tools.Launcher.Views.Shared;

public partial class NewsCollectionView : UserControl
{
    // Posts rise into place one after another when the list loads. The delay stops growing after a few
    // posts so a long feed does not keep the last ones waiting.
    private const int STAGGER_MS = 50;
    private const int STAGGER_LIMIT = 6;
    private static readonly TimeSpan ARRIVAL = TimeSpan.FromMilliseconds(300);

    public NewsCollectionView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ItemsControl>("NewsItems")!.ContainerPrepared += OnContainerPrepared;
    }

    public Border? NewsPanelBorder => this.FindControl<Border>("NewsPanel");

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Classes.Contains("motion") != true) return;
        var container = e.Container;
        var delay = TimeSpan.FromMilliseconds(Math.Min(e.Index, STAGGER_LIMIT) * STAGGER_MS);
        // Transitions rather than a keyframe animation: Avalonia has no keyframe animator for
        // RenderTransform, and a keyframe animation cannot target the transform itself.
        container.Opacity = 0;
        container.RenderTransform = TransformOperations.Parse("translateY(8px)");
        container.Transitions =
        [
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = ARRIVAL, Delay = delay, Easing = MainWindow.StandardEasing },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = ARRIVAL, Delay = delay, Easing = MainWindow.StandardEasing },
        ];
        Dispatcher.UIThread.Post(() =>
        {
            container.Opacity = 1;
            container.RenderTransform = TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Background);
    }
}
