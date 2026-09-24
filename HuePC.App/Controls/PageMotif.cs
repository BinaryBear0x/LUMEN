using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace HuePC.App.Controls;

/// <summary>One consistent illustration series, with a restrained transition between pages.</summary>
public sealed class PageMotif : Border
{
    private static readonly Dictionary<string, BitmapSource> ArtworkCache = new();
    private readonly Image _image;
    private readonly TranslateTransform _offset;

    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(string), typeof(PageMotif),
        new PropertyMetadata("Control", OnPageChanged));

    public string Page
    {
        get => (string)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public PageMotif()
    {
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;

        _offset = new TranslateTransform();
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _offset
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Child = _image;
        Loaded += (_, _) =>
        {
            SetArtwork(Page, animate: false);
            if (SystemParameters.ClientAreaAnimation)
            {
                _offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-2, 2, TimeSpan.FromSeconds(2.8))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
            }
        };
    }

    private static void OnPageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((PageMotif)sender).SetArtwork((string)args.NewValue, animate: true);

    private void SetArtwork(string page, bool animate)
    {
        var file = page switch
        {
            "Devices" => "devices",
            "Schedules" => "schedules",
            "Effects" => "effects",
            "Profiles" => "profiles",
            "Music" => "music",
            "Notifications" => "notifications",
            "Environment" => "environment",
            _ => "control"
        };
        if (!ArtworkCache.ContainsKey(file))
        {
            var artwork = new BitmapImage();
            artwork.BeginInit();
            artwork.UriSource = new Uri($"pack://application:,,,/HuePC.App;component/Assets/Illustrations/{file}.png");
            artwork.DecodePixelWidth = 620;
            artwork.CacheOption = BitmapCacheOption.OnLoad;
            artwork.EndInit();
            artwork.Freeze();
            var pixels = new byte[artwork.PixelWidth * artwork.PixelHeight * 4];
            var converted = new FormatConvertedBitmap(artwork, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(pixels, artwork.PixelWidth * 4, 0);
            var left = artwork.PixelWidth;
            var top = artwork.PixelHeight;
            var right = 0;
            var bottom = 0;
            for (var y = 0; y < artwork.PixelHeight; y++)
            {
                for (var x = 0; x < artwork.PixelWidth; x++)
                {
                    if (pixels[(y * artwork.PixelWidth + x) * 4 + 3] <= 8) continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
            if (right > left && bottom > top)
            {
                const int padding = 4;
                left = Math.Max(0, left - padding);
                top = Math.Max(0, top - padding);
                right = Math.Min(artwork.PixelWidth - 1, right + padding);
                bottom = Math.Min(artwork.PixelHeight - 1, bottom + padding);
                var cropped = new CroppedBitmap(artwork, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
                cropped.Freeze();
                ArtworkCache.Add(file, cropped);
            }
            else ArtworkCache.Add(file, artwork);
        }
        _image.Source = ArtworkCache[file];
        if (!animate || !IsLoaded || !SystemParameters.ClientAreaAnimation) return;

        _image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        _offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
