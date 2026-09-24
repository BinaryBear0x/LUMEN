using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HuePC.Core.Services;

namespace HuePC.App.Controls;

/// <summary>Small animated preview of how a bulb effect behaves (colour, flicker and speed).</summary>
public sealed class EffectPreview : FrameworkElement
{
    public static readonly DependencyProperty EffectKindProperty = DependencyProperty.Register(
        nameof(EffectKind), typeof(HueEffect), typeof(EffectPreview),
        new FrameworkPropertyMetadata(HueEffect.Candle, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
        nameof(Speed), typeof(double), typeof(EffectPreview),
        new FrameworkPropertyMetadata(50.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();

    public EffectPreview()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public HueEffect EffectKind
    {
        get => (HueEffect)GetValue(EffectKindProperty);
        set => SetValue(EffectKindProperty, value);
    }

    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var speedFactor = 0.45 + Math.Clamp(Speed, 0, 100) / 100.0 * 1.35;
        var time = _clock.Elapsed.TotalSeconds * speedFactor;

        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(0x12, 0x17, 0x1A)),
            new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x38)), 1),
            new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)), 10, 10);

        var (color, glow, sparks) = Compute(time);
        var center = new Point(width / 2, height / 2);
        var coreRadius = Math.Min(width, height) * 0.26;
        var glowRadius = coreRadius * (1.35 + glow * 0.75);
        var alpha = (byte)Math.Clamp(60 + glow * 150, 0, 255);

        var glowBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0));
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        drawingContext.DrawEllipse(glowBrush, null, center, glowRadius, glowRadius);

        var coreBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(90 + glow * 165, 0, 255), color.R, color.G, color.B));
        drawingContext.DrawEllipse(coreBrush, null, center, coreRadius, coreRadius);

        for (var index = 0; index < sparks; index++)
        {
            var angle = _random.NextDouble() * Math.PI * 2;
            var distance = coreRadius * (1.1 + _random.NextDouble() * 1.6);
            var size = 1.4 + _random.NextDouble() * 2.2;
            var sparkAlpha = (byte)(90 + _random.NextDouble() * 165);
            var point = new Point(center.X + Math.Cos(angle) * distance, center.Y + Math.Sin(angle) * distance * 0.7);
            drawingContext.DrawEllipse(new SolidColorBrush(Color.FromArgb(sparkAlpha, 255, 255, 255)), null, point, size, size);
        }
    }

    private (Color Color, double Glow, int Sparks) Compute(double time)
    {
        switch (EffectKind)
        {
            case HueEffect.Candle:
            {
                var jitter = (_random.NextDouble() - 0.5) * 0.08;
                var glow = 0.72 + 0.16 * Math.Sin(time * 8.9) + 0.08 * Math.Sin(time * 23.1) + jitter;
                return (Blend(Rgb(0xFF, 0xA6, 0x46), Rgb(0xFF, 0x78, 0x1E), 0.5 + 0.5 * Math.Sin(time * 3.1)), Math.Clamp(glow, 0.2, 1.2), 0);
            }
            case HueEffect.Fireplace:
            {
                var flare = Math.Clamp((Math.Sin(time * 1.35) - 0.82) / 0.18, 0, 1);
                var glow = 0.62 + 0.22 * Math.Sin(time * 7.1) + 0.08 * Math.Sin(time * 19.3) + flare * 0.35 + (_random.NextDouble() - 0.5) * 0.12;
                return (Blend(Rgb(0xFF, 0x5A, 0x16), Rgb(0xFF, 0xB4, 0x3C), 0.5 + 0.5 * Math.Sin(time * 2.2)), Math.Clamp(glow, 0.15, 1.35), 0);
            }
            case HueEffect.Prism:
                return (Hsl(time * 70 % 360, 0.85, 0.62), 1.0, 0);
            case HueEffect.Sparkle:
                return (Rgb(0x9A, 0xC8, 0xFF), _random.NextDouble() < 0.22 ? 1.15 : 0.3, 0);
            case HueEffect.Opal:
                return (Hsl(time * 32 % 360, 0.32, 0.72), 0.9 + 0.1 * Math.Sin(time * 2.4), 0);
            case HueEffect.Glisten:
                return (Hsl(178, 0.55, 0.55 + 0.18 * Math.Sin(time * 4.4)), 0.85 + 0.2 * Math.Sin(time * 4.4), 2);
            case HueEffect.Underwater:
                return (Hsl(196 + 16 * Math.Sin(time * 0.9), 0.72, 0.42 + 0.12 * Math.Sin(time * 1.7)), 0.8 + 0.2 * Math.Sin(time * 1.7), 0);
            case HueEffect.Cosmos:
                return (Hsl(268 + 22 * Math.Sin(time * 0.6), 0.7, 0.42 + 0.1 * Math.Sin(time * 1.1)), 0.85, 3);
            case HueEffect.Sunbeam:
                return (Rgb(0xFF, 0xD8, 0x78), 0.95 + 0.12 * Math.Sin(time * 1.2), 0);
            case HueEffect.Enchant:
                return (Hsl(300 + 120 * (0.5 + 0.5 * Math.Sin(time * 0.85)), 0.75, 0.55), 1.0, 1);
            default:
                return (Rgb(0x8A, 0x99, 0xA6), 0.35, 0);
        }
    }

    private static Color Rgb(byte red, byte green, byte blue) => Color.FromRgb(red, green, blue);

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static Color Hsl(double hue, double saturation, double lightness)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);
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

/// <summary>Animated preview of a light profile: the colour wheel travels the palette at the chosen cycle.</summary>
public sealed class ProfilePreview : FrameworkElement
{
    public static readonly DependencyProperty ColorsProperty = DependencyProperty.Register(
        nameof(Colors), typeof(IReadOnlyList<Color>), typeof(ProfilePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CycleSecondsProperty = DependencyProperty.Register(
        nameof(CycleSeconds), typeof(double), typeof(ProfilePreview),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ProfilePreview()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public IReadOnlyList<Color>? Colors
    {
        get => (IReadOnlyList<Color>?)GetValue(ColorsProperty);
        set => SetValue(ColorsProperty, value);
    }

    public double CycleSeconds
    {
        get => (double)GetValue(CycleSecondsProperty);
        set => SetValue(CycleSecondsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(0x12, 0x17, 0x1A)),
            new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x38)), 1),
            new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)), 10, 10);

        var colors = Colors;
        if (colors is null || colors.Count == 0)
        {
            return;
        }

        var cycle = Math.Clamp(CycleSeconds, 6, 120);
        var progress = _clock.Elapsed.TotalSeconds % cycle / cycle;
        var scaled = progress * colors.Count;
        var index = (int)scaled % colors.Count;
        var next = (index + 1) % colors.Count;
        var blend = scaled - Math.Floor(scaled);
        var current = Blend(colors[index], colors[next], blend);

        var center = new Point(width / 2, height * 0.38);
        var coreRadius = Math.Min(width, height) * 0.2;
        var glowRadius = coreRadius * 2.0;
        var glowBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(150, current.R, current.G, current.B), 0));
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, current.R, current.G, current.B), 1));
        drawingContext.DrawEllipse(glowBrush, null, center, glowRadius, glowRadius);
        drawingContext.DrawEllipse(new SolidColorBrush(current), null, center, coreRadius, coreRadius);

        var barRect = new Rect(16, height - 30, Math.Max(1, width - 32), 10);
        var barBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var stop = 0; stop < colors.Count; stop++)
        {
            barBrush.GradientStops.Add(new GradientStop(colors[stop], stop / (double)Math.Max(1, colors.Count - 1)));
        }

        drawingContext.DrawRoundedRectangle(barBrush, null, barRect, 5, 5);
        var knobX = barRect.X + barRect.Width * progress;
        var knob = new Point(knobX, barRect.Y + barRect.Height / 2);
        drawingContext.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, knob, 9, 9);
        drawingContext.DrawEllipse(new SolidColorBrush(current), new Pen(new SolidColorBrush(System.Windows.Media.Colors.White), 1.4), knob, 5.5, 5.5);
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}

/// <summary>Live spectrum read-out: scrolling line graph for bass, mid and treble plus a beat pulse.</summary>
public sealed class SpectrumVisualizer : FrameworkElement
{
    private const int HistoryLength = 120;

    public static readonly DependencyProperty BassProperty = DependencyProperty.Register(
        nameof(Bass), typeof(double), typeof(SpectrumVisualizer), new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty MidProperty = DependencyProperty.Register(
        nameof(Mid), typeof(double), typeof(SpectrumVisualizer), new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty TrebleProperty = DependencyProperty.Register(
        nameof(Treble), typeof(double), typeof(SpectrumVisualizer), new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty BeatProperty = DependencyProperty.Register(
        nameof(Beat), typeof(double), typeof(SpectrumVisualizer), new FrameworkPropertyMetadata(0.0));

    private static readonly Color BassColor = Color.FromRgb(0xFF, 0x6B, 0x4A);
    private static readonly Color MidColor = Color.FromRgb(0x7A, 0xD8, 0x5B);
    private static readonly Color TrebleColor = Color.FromRgb(0x5B, 0x9B, 0xFF);

    private readonly DispatcherTimer _timer;
    private readonly double[] _bassHistory = new double[HistoryLength];
    private readonly double[] _midHistory = new double[HistoryLength];
    private readonly double[] _trebleHistory = new double[HistoryLength];
    private double _displayBass;
    private double _displayMid;
    private double _displayTreble;
    private double _displayBeat;
    private int _historyHead;
    private int _historyCount;

    public SpectrumVisualizer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Step();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public double Bass
    {
        get => (double)GetValue(BassProperty);
        set => SetValue(BassProperty, value);
    }

    public double Mid
    {
        get => (double)GetValue(MidProperty);
        set => SetValue(MidProperty, value);
    }

    public double Treble
    {
        get => (double)GetValue(TrebleProperty);
        set => SetValue(TrebleProperty, value);
    }

    public double Beat
    {
        get => (double)GetValue(BeatProperty);
        set => SetValue(BeatProperty, value);
    }

    private void Step()
    {
        _displayBass += (Math.Clamp(Bass, 0, 1) - _displayBass) * 0.32;
        _displayMid += (Math.Clamp(Mid, 0, 1) - _displayMid) * 0.32;
        _displayTreble += (Math.Clamp(Treble, 0, 1) - _displayTreble) * 0.32;
        _displayBeat = Math.Max(_displayBeat * 0.9, Math.Clamp(Beat, 0, 1));

        _bassHistory[_historyHead] = _displayBass;
        _midHistory[_historyHead] = _displayMid;
        _trebleHistory[_historyHead] = _displayTreble;
        _historyHead = (_historyHead + 1) % HistoryLength;
        if (_historyCount < HistoryLength)
        {
            _historyCount++;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(0x12, 0x17, 0x1A)),
            new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x38)), 1),
            new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)), 10, 10);

        var left = 14.0;
        var right = width - 14;
        var top = 14.0;
        var bottom = height - 14;
        var plotWidth = Math.Max(1, right - left);
        var plotHeight = Math.Max(1, bottom - top);

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 0x8A, 0x99, 0xA6)), 1);
        for (var line = 0; line <= 2; line++)
        {
            var y = top + plotHeight * line / 2.0;
            drawingContext.DrawLine(gridPen, new Point(left, y), new Point(right, y));
        }

        DrawSeries(drawingContext, _bassHistory, BassColor, left, top, plotWidth, plotHeight);
        DrawSeries(drawingContext, _midHistory, MidColor, left, top, plotWidth, plotHeight);
        DrawSeries(drawingContext, _trebleHistory, TrebleColor, left, top, plotWidth, plotHeight);

        var pulse = new Point(right - 6, top + 6);
        var pulseRadius = 5 + _displayBeat * 8;
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromArgb((byte)(40 + _displayBeat * 180), 0xFF, 0xD8, 0x78)),
            null, pulse, pulseRadius, pulseRadius);
    }

    private void DrawSeries(DrawingContext drawingContext, double[] history, Color color, double left, double top, double plotWidth, double plotHeight)
    {
        if (_historyCount < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var started = false;
            for (var index = 0; index < _historyCount; index++)
            {
                // Newest sample sits at the right edge; older samples scroll to the left.
                var age = _historyCount - 1 - index;
                var sampleIndex = (_historyHead - 1 - age + HistoryLength * 2) % HistoryLength;
                var value = Math.Clamp(history[sampleIndex], 0, 1);
                var x = left + plotWidth * index / (double)(HistoryLength - 1);
                var y = top + plotHeight * (0.97 - value * 0.9);
                var point = new Point(x, y);
                if (!started)
                {
                    context.BeginFigure(point, false, false);
                    started = true;
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B)), 2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        drawingContext.DrawGeometry(null, pen, geometry);
    }
}
