using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Ksp2Redux.Tools.Launcher.Controls;

public class CanvasImageLayer(Bitmap bitmap, ParallaxLayout layout, LayerPlacement placement) : Control
{
    public override void Render(DrawingContext context)
    {
        var (scale, origin) = layout.Fit(Bounds.Size);
        context.DrawImage(bitmap, new Rect(
            origin.X + placement.X * scale,
            origin.Y + placement.Y * scale,
            bitmap.PixelSize.Width * scale,
            bitmap.PixelSize.Height * scale));
    }
}
