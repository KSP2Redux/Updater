using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace Ksp2Redux.Tools.Launcher.Controls;

public class ParallaxBackground : Panel
{
    public const double OVERSCAN = 1.04;
    public const double STAR_DEPTH = 4;
    public const double PLANET_DEPTH = 8;
    public const double STATION_DEPTH = 18;

    public static readonly StyledProperty<Vector> OffsetProperty =
        AvaloniaProperty.Register<ParallaxBackground, Vector>(nameof(Offset));

    public static readonly StyledProperty<bool> ShowStarsProperty =
        AvaloniaProperty.Register<ParallaxBackground, bool>(nameof(ShowStars), true);

    private static readonly Lazy<Bitmap> PlanetBitmap = new(() => Load("planet.png"));
    private static readonly Lazy<Bitmap> StationBitmap = new(() => Load("station.png"));

    private readonly List<(Control Layer, TranslateTransform Translate, double Depth)> _layers = [];
    private readonly Starfield _stars = new();
    private readonly StationLayer _station = new(StationBitmap.Value);

    public ParallaxBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        AddLayer(_stars, STAR_DEPTH);
        AddLayer(new Image { Source = PlanetBitmap.Value, Stretch = Stretch.UniformToFill }, PLANET_DEPTH);
        AddLayer(_station, STATION_DEPTH);
    }

    public double LaunchTime
    {
        get => _station.LaunchTime;
        set => _station.LaunchTime = value;
    }

    public Vector Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    public bool ShowStars
    {
        get => GetValue(ShowStarsProperty);
        set => SetValue(ShowStarsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OffsetProperty)
        {
            foreach (var (_, translate, depth) in _layers)
            {
                translate.X = -Offset.X * depth;
                translate.Y = -Offset.Y * depth;
            }
        }
        else if (change.Property == ShowStarsProperty)
        {
            _stars.IsVisible = ShowStars;
        }
    }

    private void AddLayer(Control layer, double depth)
    {
        var translate = new TranslateTransform();
        layer.RenderTransformOrigin = RelativePoint.Center;
        layer.RenderTransform = new TransformGroup { Children = { new ScaleTransform(OVERSCAN, OVERSCAN), translate } };
        _layers.Add((layer, translate, depth));
        Children.Add(layer);
    }

    private static Bitmap Load(string name) =>
        new(AssetLoader.Open(new Uri($"avares://Ksp2Redux.Tools.Launcher/Assets/Parallax/{name}")));

    private sealed class Starfield : Control
    {
        private const int STARS_PER_MEGAPIXEL = 420;

        private static readonly (double X, double Y, double Radius, IBrush Brush)[] Stars = CreateStars();

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            var count = (int)Math.Min(Stars.Length, size.Width * size.Height / 1_000_000 * STARS_PER_MEGAPIXEL);
            for (var i = 0; i < count; i++)
            {
                var (x, y, radius, brush) = Stars[i];
                context.DrawEllipse(brush, null, new Point(x * size.Width, y * size.Height), radius, radius);
            }
        }

        private static (double, double, double, IBrush)[] CreateStars()
        {
            var random = new Random(2026);
            return Enumerable.Range(0, 3000).Select(_ =>
            {
                var brightness = Math.Pow(random.NextDouble(), 2.2);
                var radius = 0.35 + brightness * 0.9;
                var alpha = (byte)(40 + brightness * 200);
                var tint = random.NextDouble();
                var color = tint < 0.15
                    ? Color.FromArgb(alpha, 190, 210, 255)
                    : tint > 0.9 ? Color.FromArgb(alpha, 255, 225, 190) : Color.FromArgb(alpha, 255, 255, 255);
                return (random.NextDouble(), random.NextDouble(), radius, (IBrush)new ImmutableSolidColorBrush(color));
            }).ToArray();
        }
    }
}
