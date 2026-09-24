using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HuePC.App.Controls;

/// <summary>
/// Continuous hue/saturation picker drawn from a generated bitmap. The user drags anywhere on the
/// wheel and <see cref="ColorChanged"/> reports the selected color; <see cref="SelectedColor"/>
/// only moves the thumb so remote state can be shown without fighting the pointer.
/// </summary>
public sealed class ColorWheel : FrameworkElement
{
    private const int ImageSize = 220;
    private static ImageSource? _wheelImage;
    private Point? _thumb;
    private bool _dragging;

    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor),
        typeof(Color),
        typeof(ColorWheel),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender, OnSelectedColorChanged));

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public event EventHandler<Color>? ColorChanged;

    public ColorWheel()
    {
        Focusable = false;
        Cursor = Cursors.Cross;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        drawingContext.DrawImage(WheelImage, new Rect(0, 0, size, size));

        var position = _thumb ?? PositionFromColor(SelectedColor);
        if (position is { } normalized)
        {
            var center = new Point(normalized.X * size, normalized.Y * size);
            drawingContext.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 2.4), center, 8, 8);
            drawingContext.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), 1), center, 5.6, 5.6);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        CaptureMouse();
        UpdateFromPoint(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            UpdateFromPoint(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    private void UpdateFromPoint(Point point)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var centre = size / 2;
        var radius = centre - 1;
        var dx = point.X - centre;
        var dy = point.Y - centre;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var saturation = Math.Min(1, distance / radius);
        var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;

        _thumb = new Point(0.5 + Math.Cos(hue * Math.PI / 180) * saturation * 0.5,
                           0.5 + Math.Sin(hue * Math.PI / 180) * saturation * 0.5);
        InvalidateVisual();
        ColorChanged?.Invoke(this, ColorFromHsv(hue, saturation, 1));
    }

    private static void OnSelectedColorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var wheel = (ColorWheel)sender;
        if (!wheel._dragging)
        {
            wheel._thumb = PositionFromColor((Color)args.NewValue);
            wheel.InvalidateVisual();
        }
    }

    private static Point? PositionFromColor(Color color)
    {
        var (hue, saturation) = ToHsv(color);
        if (saturation <= 0.01)
        {
            return new Point(0.5, 0.5);
        }

        var radians = hue * Math.PI / 180;
        return new Point(0.5 + Math.Cos(radians) * saturation * 0.5, 0.5 + Math.Sin(radians) * saturation * 0.5);
    }

    private static ImageSource WheelImage => _wheelImage ??= CreateWheelImage();

    private static ImageSource CreateWheelImage()
    {
        var bitmap = new WriteableBitmap(ImageSize, ImageSize, 96, 96, PixelFormats.Pbgra32, null);
        var stride = ImageSize * 4;
        var pixels = new byte[ImageSize * ImageSize * 4];
        var centre = ImageSize / 2.0;
        var radius = centre - 1;

        for (var y = 0; y < ImageSize; y++)
        {
            for (var x = 0; x < ImageSize; x++)
            {
                var index = y * stride + x * 4;
                var dx = x + 0.5 - centre;
                var dy = y + 0.5 - centre;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > radius)
                {
                    continue;
                }

                var saturation = Math.Min(1, distance / radius);
                var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var color = ColorFromHsv(hue, saturation, 1);
                var alpha = distance > radius - 1.2
                    ? (byte)Math.Clamp(255 * (radius - distance) / 1.2, 0, 255)
                    : (byte)255;
                pixels[index] = (byte)(color.B * alpha / 255);
                pixels[index + 1] = (byte)(color.G * alpha / 255);
                pixels[index + 2] = (byte)(color.R * alpha / 255);
                pixels[index + 3] = alpha;
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, ImageSize, ImageSize), pixels, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static (double Hue, double Saturation) ToHsv(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (Math.Abs(max - r) < double.Epsilon) hue = 60 * (((g - b) / delta) % 6);
            else if (Math.Abs(max - g) < double.Epsilon) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }

        if (hue < 0) hue += 360;
        var saturation = max <= 0 ? 0 : delta / max;
        return (hue, saturation);
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var h = hue / 60;
        var x = chroma * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        var m = value - chroma;
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
