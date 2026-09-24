using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HuePC.App.Controls;

/// <summary>Four bar signal indicator fed by the RSSI of a discovered device.</summary>
public sealed class SignalBars : FrameworkElement
{
    public static readonly DependencyProperty RssiProperty = DependencyProperty.Register(
        nameof(Rssi), typeof(double), typeof(SignalBars),
        new FrameworkPropertyMetadata(-100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Rssi
    {
        get => (double)GetValue(RssiProperty);
        set => SetValue(RssiProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var level = Rssi switch
        {
            >= -55 => 4,
            >= -65 => 3,
            >= -78 => 2,
            _ => 1
        };
        var color = level >= 3 ? Color.FromRgb(0x7A, 0xD8, 0x5B)
            : level == 2 ? Color.FromRgb(0xFF, 0xC4, 0x00)
            : Color.FromRgb(0xFF, 0x6B, 0x4A);
        var muted = Color.FromArgb(60, color.R, color.G, color.B);

        var barWidth = Math.Max(2, (width - 9) / 4.0);
        for (var index = 0; index < 4; index++)
        {
            var barHeight = height * (0.3 + index * 0.233);
            var x = index * (barWidth + 3);
            var rect = new Rect(x, height - barHeight, barWidth, barHeight);
            var brush = new SolidColorBrush(index < level ? color : muted);
            drawingContext.DrawRoundedRectangle(brush, null, rect, 1.5, 1.5);
        }
    }
}

/// <summary>Twelve segment chroma ring that highlights the note the music mapping heard last.</summary>
public sealed class ChromaWheel : FrameworkElement
{
    private static readonly string[] NoteNames = ["Do", "Do#", "Re", "Re#", "Mi", "Fa", "Fa#", "Sol", "Sol#", "La", "La#", "Si"];

    public static readonly DependencyProperty ChromaProperty = DependencyProperty.Register(
        nameof(Chroma), typeof(IReadOnlyList<double>), typeof(ChromaWheel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Chroma
    {
        get => (IReadOnlyList<double>?)GetValue(ChromaProperty);
        set => SetValue(ChromaProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var chroma = Chroma;
        var center = new Point(width / 2, height / 2);
        var outer = Math.Min(width, height) / 2 - 6;
        var inner = outer * 0.52;
        var top = 0;
        var topValue = -1.0;
        for (var index = 0; index < 12; index++)
        {
            var value = chroma is { Count: 12 } ? Math.Clamp(chroma[index], 0, 1) : 0;
            if (value > topValue)
            {
                topValue = value;
                top = index;
            }
        }

        for (var index = 0; index < 12; index++)
        {
            var value = chroma is { Count: 12 } ? Math.Clamp(chroma[index], 0, 1) : 0;
            var start = -90 + index * 30 + 1.5;
            var end = -90 + (index + 1) * 30 - 1.5;
            var color = Hsl(index * 30, 0.65, 0.28 + value * 0.34);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(PointOnCircle(center, outer, start), true, true);
                context.ArcTo(PointOnCircle(center, outer, end), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(PointOnCircle(center, inner, end), true, false);
                context.ArcTo(PointOnCircle(center, inner, start), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(new SolidColorBrush(color), null, geometry);
            if (index == top && topValue > 0.05)
            {
                drawingContext.DrawGeometry(null, new Pen(new SolidColorBrush(Colors.White), 1.4), geometry);
            }
        }

        var label = chroma is { Count: 12 } && topValue > 0.05 ? NoteNames[top] : "—";
        var text = new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text, Segoe UI"),
            15,
            new SolidColorBrush(Color.FromRgb(0xE7, 0xF3, 0xE9)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static Color Hsl(double hue, double saturation, double lightness)
    {
        hue = (hue % 360 + 360) % 360;
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
