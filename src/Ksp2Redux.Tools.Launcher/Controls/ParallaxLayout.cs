using System.Text.Json;
using Avalonia;

namespace Ksp2Redux.Tools.Launcher.Controls;

public sealed record LayerPlacement(int X, int Y);

public sealed record ParallaxLayout(int CanvasWidth, int CanvasHeight, LayerPlacement Planet, LayerPlacement Station)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ParallaxLayout Load(Stream json) =>
        JsonSerializer.Deserialize<ParallaxLayout>(json, Options)
        ?? throw new InvalidDataException("The parallax layout is empty.");

    public (double Scale, Point Origin) Fit(Size bounds)
    {
        var scale = Math.Max(bounds.Width / CanvasWidth, bounds.Height / CanvasHeight);
        return (scale, new Point((bounds.Width - CanvasWidth * scale) / 2, (bounds.Height - CanvasHeight * scale) / 2));
    }
}
