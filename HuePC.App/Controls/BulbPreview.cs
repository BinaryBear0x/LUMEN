using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HuePC.Core.Services;

namespace HuePC.App.Controls;

/// <summary>
/// Bulb illustration that follows the selected color, power and brightness. Dragging on the bulb
/// changes the brightness (vertical) and the white tone (horizontal).
/// </summary>
public sealed class BulbPreview : FrameworkElement
{
    private static readonly Geometry Glass = Geometry.Parse(
        "M 160,52 C 205,52 240,87 240,131 C 240,165 220,185 207,201 C 201,209 196,218 196,229 L 124,229 C 124,218 119,209 113,201 C 100,185 80,165 80,131 C 80,87 115,52 160,52 Z");
    private static readonly Brush MetalDark = Brush(Color.FromRgb(62, 73, 84));
    private static readonly Brush MetalLight = Brush(Color.FromRgb(151, 163, 173));
    private static readonly Pen Outline = new(Brush(Color.FromRgb(90, 103, 114)), 2);
    private readonly Stopwatch _clock = new();
    private double _lastSeconds;
    private double _light;
    private double _r = 255, _g = 218, _b = 170;
    private Point _dragOrigin;
    private double _dragBrightness;
    private double _dragTemperature;
    private bool _dragging;

    public static readonly DependencyProperty PreviewColorProperty = DependencyProperty.Register(
        nameof(PreviewColor), typeof(Color), typeof(BulbPreview),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(
        nameof(Brightness), typeof(double), typeof(BulbPreview),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(BulbPreview),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsColorModeProperty = DependencyProperty.Register(
        nameof(IsColorMode), typeof(bool), typeof(BulbPreview),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorTemperatureProperty = DependencyProperty.Register(
        nameof(ColorTemperature), typeof(double), typeof(BulbPreview),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(BulbPreview), new PropertyMetadata(true));
    public static readonly DependencyProperty EditableBrightnessProperty = DependencyProperty.Register(
        nameof(EditableBrightness), typeof(double), typeof(BulbPreview),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty EditableTemperatureProperty = DependencyProperty.Register(
        nameof(EditableTemperature), typeof(double), typeof(BulbPreview),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public Color PreviewColor { get => (Color)GetValue(PreviewColorProperty); set => SetValue(PreviewColorProperty, value); }
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public bool IsColorMode { get => (bool)GetValue(IsColorModeProperty); set => SetValue(IsColorModeProperty, value); }
    public double ColorTemperature { get => (double)GetValue(ColorTemperatureProperty); set => SetValue(ColorTemperatureProperty, value); }
    public bool IsEditable { get => (bool)GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }
    public double EditableBrightness { get => (double)GetValue(EditableBrightnessProperty); set => SetValue(EditableBrightnessProperty, value); }
    public double EditableTemperature { get => (double)GetValue(EditableTemperatureProperty); set => SetValue(EditableTemperatureProperty, value); }

    private Color TargetColor
    {
        get
        {
            if (IsColorMode) return PreviewColor;
            var temperature = HueColorConverter.FromMired((ushort)Math.Clamp(Math.Round(ColorTemperature), 154, 455));
            return Color.FromRgb(temperature.R, temperature.G, temperature.B);
        }
    }

    public BulbPreview()
    {
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEditable || !IsOn)
        {
            return;
        }

        _dragOrigin = e.GetPosition(this);
        _dragBrightness = EditableBrightness;
        _dragTemperature = EditableTemperature;
        _dragging = true;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        var position = e.GetPosition(this);
        var deltaX = position.X - _dragOrigin.X;
        var deltaY = position.Y - _dragOrigin.Y;

        EditableBrightness = Math.Clamp(_dragBrightness - deltaY / 2.6, 1, 100);
        EditableTemperature = Math.Clamp(_dragTemperature + deltaX * 1.1, 154, 455);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateAnimation()
    {
        if (IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation)
        {
            if (_clock.IsRunning) return;
            _lastSeconds = 0;
            _clock.Restart();
            CompositionTarget.Rendering += OnFrame;
        }
        else StopAnimation();
    }

    private void StopAnimation()
    {
        CompositionTarget.Rendering -= OnFrame;
        _clock.Stop();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = Math.Clamp(now - _lastSeconds, 0, .08);
        _lastSeconds = now;
        var blend = 1 - Math.Exp(-elapsed * 9);
        var target = TargetColor;
        _r += (target.R - _r) * blend;
        _g += (target.G - _g) * blend;
        _b += (target.B - _b) * blend;
        _light += ((IsOn ? Math.Clamp(Brightness, 0, 100) / 100 : 0) - _light) * blend;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (!_clock.IsRunning)
        {
            var target = TargetColor;
            _r = target.R; _g = target.G; _b = target.B;
            _light = IsOn ? Math.Clamp(Brightness, 0, 100) / 100 : 0;
        }
        dc.PushTransform(new ScaleTransform(ActualWidth / 320, ActualHeight / 360));
        var rgb = Color.FromRgb((byte)_r, (byte)_g, (byte)_b);
        var intensity = Math.Pow(_light, .72);
        var halo = Color.FromArgb((byte)(105 * intensity), rgb.R, rgb.G, rgb.B);
        var glow = new RadialGradientBrush(Color.FromArgb((byte)(100 * intensity), rgb.R, rgb.G, rgb.B), Colors.Transparent);
        dc.DrawEllipse(glow, null, new Point(160, 145), 133, 133);
        dc.DrawEllipse(Brush(Color.FromArgb((byte)(43 * intensity), rgb.R, rgb.G, rgb.B)), null,
            new Point(160, 148), 101, 101);

        var glass = new RadialGradientBrush
        {
            Center = new Point(.42, .35), GradientOrigin = new Point(.38, .27),
            RadiusX = .74, RadiusY = .79
        };
        var shade = (byte)Math.Clamp(65 + intensity * 150, 0, 255);
        glass.GradientStops.Add(new GradientStop(
            Color.FromRgb((byte)Math.Min(255, 88 + rgb.R * intensity * .67),
                (byte)Math.Min(255, 94 + rgb.G * intensity * .67),
                (byte)Math.Min(255, 100 + rgb.B * intensity * .67)), 0));
        glass.GradientStops.Add(new GradientStop(Color.FromRgb(
            (byte)Math.Min(255, 49 + rgb.R * intensity * .51),
            (byte)Math.Min(255, 60 + rgb.G * intensity * .51),
            (byte)Math.Min(255, 72 + rgb.B * intensity * .51)), .75));
        glass.GradientStops.Add(new GradientStop(Color.FromRgb(41, 52, 61), 1));
        dc.DrawGeometry(glass, Outline, Glass);

        if (intensity > .01)
        {
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(shade, 255, 255, 255), Colors.Transparent),
                null, new Point(145, 125), 61, 58);
            dc.DrawLine(new Pen(Brush(Color.FromArgb((byte)(115 * intensity), rgb.R, rgb.G, rgb.B)), 2),
                new Point(131, 183), new Point(189, 183));
        }
        dc.DrawGeometry(Brush(Color.FromArgb(52, 255, 255, 255)), null,
            Geometry.Parse("M 113,121 C 117,94 135,75 156,72 C 137,84 125,103 121,128 Z"));

        dc.DrawRoundedRectangle(MetalDark, Outline, new Rect(121, 224, 78, 17), 5, 5);
        for (var i = 0; i < 4; i++)
        {
            dc.DrawRoundedRectangle(i % 2 == 0 ? MetalLight : MetalDark, Outline,
                new Rect(127 + i * 4, 243 + i * 15, 66 - i * 8, 13), 4, 4);
        }
        dc.DrawEllipse(Brush(Color.FromArgb((byte)(110 * intensity), halo.R, halo.G, halo.B)), null,
            new Point(160, 323), 56, 7);
        dc.Pop();
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
