using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace Ksp2Redux.Tools.Launcher.Controls;

public class StationLayer : Control
{
    private static readonly Vector Axis = new(0.898, 0.439);
    private static readonly Point EngineCentre = new(1908, 884);
    private static readonly Point[] Nozzles = [new(5, -28), new(8, -3), new(-3, 35)];
    private const double PLUME_LENGTH = 470;
    private const double PLUME_HALF_ANGLE = 0.66;
    private const int PLUME_RAYS = 70;
    private const int PLUME_COLUMN = 10;

    private static readonly Color PlumeCore = Color.FromRgb(120, 175, 255);
    private static readonly Color PlumeEdge = Color.FromRgb(30, 85, 250);
    private static readonly IBrush NozzleCore = new ImmutableSolidColorBrush(Color.FromArgb(235, 255, 255, 255));

    private readonly Bitmap _bitmap;
    private double _launchTime;
    private LaunchFrame _frame = LaunchFrame.Rest;

    public StationLayer(Bitmap bitmap)
    {
        _bitmap = bitmap;
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    public LaunchFrame Frame => _frame;

    public void SetLaunch(double time, LaunchFrame frame)
    {
        if (frame == _frame && (frame.Thrust <= 0 || time == _launchTime)) return;
        _launchTime = time;
        _frame = frame;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = _bitmap.PixelSize.Width;
        var height = _bitmap.PixelSize.Height;
        var scale = Math.Max(Bounds.Width / width, Bounds.Height / height);
        var origin = new Point((Bounds.Width - width * scale) / 2, (Bounds.Height - height * scale) / 2);

        var t = _launchTime;
        var frame = _frame;
        if (frame.StationOpacity <= 0) return;

        var rumble = frame.Thrust > 0 ? new Vector(Noise(t * 3, 1) - 0.5, Noise(t * 3, 2) - 0.5) * 2.4 * frame.Thrust : default;
        var travel = -Axis * frame.Travel + rumble;

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y)))
        using (context.PushTransform(Matrix.CreateTranslation(travel.X, travel.Y)))
        using (context.PushOpacity(frame.StationOpacity))
        {
            var thrust = frame.Thrust * (1 + frame.Flash * 0.35);
            if (thrust > 0) DrawExhaust(context, t, thrust, frame.Burn);
            context.DrawImage(_bitmap, new Rect(0, 0, width, height));
            if (thrust > 0) DrawBloom(context, t, thrust, frame.Flash);
        }
    }

    private static void DrawExhaust(DrawingContext context, double t, double thrust, double burn)
    {
        var angle = Math.Atan2(Axis.Y, Axis.X);
        using var local = context.PushTransform(Matrix.CreateRotation(angle) * Matrix.CreateTranslation(EngineCentre.X, EngineCentre.Y));

        var length = PLUME_LENGTH * thrust * (1 + 0.3 * burn) * (0.95 + 0.1 * Noise(t, 7));

        for (var i = PLUME_COLUMN - 1; i >= 0; i--)
        {
            var f = i / (double)(PLUME_COLUMN - 1);
            var radius = (18 + length * 0.42 * f) * (0.94 + 0.12 * Noise(t + i * 0.41, i));
            var alpha = thrust * Lerp(0.32, 0.04, Math.Pow(f, 0.7));
            context.DrawEllipse(Radial(Lerp(PlumeCore, PlumeEdge, f), alpha), null, new Point(length * 0.62 * f, 3), radius * 1.15, radius);
        }

        for (var r = 0; r < PLUME_RAYS; r++)
        {
            var spread = Hash(r, 5) * 2 - 1;
            var fan = Math.Sign(spread) * Math.Pow(Math.Abs(spread), 1.1);
            var rayAngle = (fan * PLUME_HALF_ANGLE + (Noise(t * 0.35, r + 60) - 0.5) * 0.04);
            var edge = Math.Abs(fan);
            var rayLength = length * (0.55 + 0.45 * Hash(r, 6)) * (0.85 + 0.3 * Noise(t * 1.1, r)) * (1 - 0.25 * edge);
            var halfWidth = rayLength * (0.025 + 0.045 * Hash(r, 7));
            var alpha = thrust * (0.09 + 0.08 * Hash(r, 8)) * (1 - 0.55 * edge);
            var nozzle = Nozzles[r % Nozzles.Length];

            using (context.PushTransform(Matrix.CreateRotation(rayAngle) * Matrix.CreateTranslation(nozzle.X, nozzle.Y)))
            {
                context.DrawGeometry(Linear(Lerp(PlumeCore, PlumeEdge, edge), alpha), null, Ray(rayLength, halfWidth));
            }
        }

        foreach (var nozzle in Nozzles)
        {
            var hot = Math.Min(thrust, 1) * (0.85 + 0.15 * Noise(t * 1.6, nozzle.Y));
            using (context.PushTransform(Matrix.CreateTranslation(nozzle.X, nozzle.Y)))
            {
                context.DrawGeometry(Linear(Rgb(235, 245, 255), 0.8 * hot), null, Ray(34 * hot, 5));
            }
            context.DrawEllipse(Radial(Rgb(150, 195, 255), 0.7 * hot), null, nozzle, 24, 24);
            context.DrawEllipse(Radial(Rgb(255, 255, 255), hot), null, nozzle, 9, 9);
            context.DrawEllipse(NozzleCore, null, nozzle, 3, 3);
        }
    }

    private static void DrawBloom(DrawingContext context, double t, double thrust, double flash)
    {
        var alpha = Math.Min(1, 0.3 * thrust * (0.9 + 0.2 * Noise(t * 1.7, 40)) + 0.5 * flash);
        var bloom = EngineCentre + Axis * 10;
        context.DrawEllipse(Radial(Rgb(120, 170, 255), alpha), null, bloom, 130 + 70 * flash, 130 + 70 * flash);
    }

    private static StreamGeometry Ray(double length, double halfWidth)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(new Point(0, -halfWidth * 0.15), true);
        ctx.QuadraticBezierTo(new Point(length * 0.5, -halfWidth * 0.75), new Point(length, -halfWidth));
        ctx.QuadraticBezierTo(new Point(length * 1.04, 0), new Point(length, halfWidth));
        ctx.QuadraticBezierTo(new Point(length * 0.5, halfWidth * 0.75), new Point(0, halfWidth * 0.15));
        ctx.EndFigure(true);
        return geometry;
    }

    private static IBrush Radial(Color color, double alpha) => new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(WithAlpha(color, alpha), 0),
            new GradientStop(WithAlpha(color, alpha * 0.45), 0.45),
            new GradientStop(WithAlpha(color, 0), 1),
        }
    };

    private static IBrush Linear(Color color, double alpha) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(WithAlpha(color, alpha), 0),
            new GradientStop(WithAlpha(color, alpha * 0.6), 0.45),
            new GradientStop(WithAlpha(PlumeEdge, 0), 1),
        }
    };

    private static double Noise(double t, double seed) =>
        0.5 + 0.22 * Math.Sin(t * 31 + seed) + 0.16 * Math.Sin(t * 57.3 + seed * 2.1) + 0.12 * Math.Sin(t * 93.7 + seed * 3.7);

    private static double Hash(int i, int salt) => Frac(Math.Sin(i * 12.9898 + salt * 78.233) * 43758.5453);

    private static double Frac(double x) => x - Math.Floor(x);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Lerp(Color a, Color b, double f) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));

    private static double Lerp(double a, double b, double f) => a + (b - a) * f;

    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb(ToByte(alpha), color.R, color.G, color.B);

    private static byte ToByte(double alpha) => (byte)Math.Clamp(alpha * 255, 0, 255);
}
