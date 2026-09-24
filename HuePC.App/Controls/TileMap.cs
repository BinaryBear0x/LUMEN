using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HuePC.App.Controls;

/// <summary>
/// Minimal OpenStreetMap tile viewer used to pick a location. Drag to pan, wheel to zoom and click
/// to drop the marker. Tiles are cached in memory for the session.
/// </summary>
public sealed class TileMap : FrameworkElement
{
    private const int TileSize = 256;
    private const int MaximumCachedTiles = 128;
    private static readonly HttpClient Http = CreateClient();
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CachedTile> Cache = new();
    private static readonly LinkedList<string> CacheRecency = new();
    private static readonly ConcurrentDictionary<string, byte> Pending = new();
    private Point _dragOrigin;
    private double _dragLatitude;
    private double _dragLongitude;
    private bool _dragging;
    private bool _moved;

    public static readonly DependencyProperty CenterLatitudeProperty = DependencyProperty.Register(
        nameof(CenterLatitude), typeof(double), typeof(TileMap),
        new FrameworkPropertyMetadata(39.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CenterLongitudeProperty = DependencyProperty.Register(
        nameof(CenterLongitude), typeof(double), typeof(TileMap),
        new FrameworkPropertyMetadata(35.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(int), typeof(TileMap),
        new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MarkerLatitudeProperty = DependencyProperty.Register(
        nameof(MarkerLatitude), typeof(double), typeof(TileMap),
        new FrameworkPropertyMetadata(41.01, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty MarkerLongitudeProperty = DependencyProperty.Register(
        nameof(MarkerLongitude), typeof(double), typeof(TileMap),
        new FrameworkPropertyMetadata(28.98, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public TileMap()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public double CenterLatitude { get => (double)GetValue(CenterLatitudeProperty); set => SetValue(CenterLatitudeProperty, value); }
    public double CenterLongitude { get => (double)GetValue(CenterLongitudeProperty); set => SetValue(CenterLongitudeProperty, value); }
    public int Zoom { get => (int)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double MarkerLatitude { get => (double)GetValue(MarkerLatitudeProperty); set => SetValue(MarkerLatitudeProperty, value); }
    public double MarkerLongitude { get => (double)GetValue(MarkerLongitudeProperty); set => SetValue(MarkerLongitudeProperty, value); }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LUMEN/1.0 (Windows desktop app)");
        return client;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x20)), null, new Rect(0, 0, width, height));

        var zoom = Math.Clamp(Zoom, 3, 12);
        var center = LatLonToWorld(CenterLatitude, CenterLongitude, zoom);
        var originX = center.X - width / 2;
        var originY = center.Y - height / 2;
        var tileCount = 1 << zoom;
        var firstTileX = (int)Math.Floor(originX / TileSize);
        var firstTileY = (int)Math.Floor(originY / TileSize);
        var lastTileX = (int)Math.Floor((originX + width) / TileSize);
        var lastTileY = (int)Math.Floor((originY + height) / TileSize);

        for (var tileX = firstTileX; tileX <= lastTileX; tileX++)
        {
            for (var tileY = firstTileY; tileY <= lastTileY; tileY++)
            {
                if (tileX < 0 || tileY < 0 || tileX >= tileCount || tileY >= tileCount)
                {
                    continue;
                }

                var screenX = tileX * TileSize - originX;
                var screenY = tileY * TileSize - originY;
                var key = $"{zoom}/{tileX}/{tileY}";
                var tile = GetCachedTile(key);
                if (tile is not null)
                {
                    drawingContext.DrawImage(tile, new Rect(screenX, screenY, TileSize, TileSize));
                }
                else
                {
                    drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x26, 0x2B)), null, new Rect(screenX, screenY, TileSize, TileSize));
                    RequestTile(key, zoom, tileX, tileY);
                }
            }
        }

        var marker = LatLonToWorld(MarkerLatitude, MarkerLongitude, zoom);
        var markerX = marker.X - originX;
        var markerY = marker.Y - originY;
        if (markerX > -20 && markerX < width + 20 && markerY > -20 && markerY < height + 20)
        {
            var pin = new StreamGeometry();
            using (var context = pin.Open())
            {
                context.BeginFigure(new Point(markerX, markerY), true, true);
                context.LineTo(new Point(markerX - 7, markerY - 16), true, false);
                context.LineTo(new Point(markerX + 7, markerY - 16), true, false);
            }

            pin.Freeze();
            drawingContext.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xE8, 0x5B, 0x3A)), null, pin);
            drawingContext.DrawEllipse(new SolidColorBrush(Colors.White), null, new Point(markerX, markerY - 18), 5, 5);
        }

        var credit = new FormattedText(
            "© OpenStreetMap katkıcıları",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10,
            new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null,
            new Rect(width - credit.Width - 10, height - credit.Height - 6, credit.Width + 8, credit.Height + 4));
        drawingContext.DrawText(credit, new Point(width - credit.Width - 6, height - credit.Height - 4));
    }

    private void RequestTile(string key, int zoom, int tileX, int tileY)
    {
        if (!Pending.TryAdd(key, 0))
        {
            return;
        }

        _ = LoadTileAsync(key, zoom, tileX, tileY);
    }

    private async Task LoadTileAsync(string key, int zoom, int tileX, int tileY)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://tile.openstreetmap.org/{zoom}/{tileX}/{tileY}.png").ConfigureAwait(false);
            var image = new BitmapImage();
            image.BeginInit();
            using (var stream = new MemoryStream(bytes))
            {
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
            }

            image.Freeze();
            await Dispatcher.InvokeAsync(() =>
            {
                AddCachedTile(key, image);
                InvalidateVisual();
            }).Task.ConfigureAwait(false);
        }
        catch
        {
            // Offline or rate limited: the placeholder stays.
        }
        finally
        {
            Pending.TryRemove(key, out _);
        }
    }

    private static BitmapSource? GetCachedTile(string key)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(key, out var cached))
            {
                return null;
            }

            CacheRecency.Remove(cached.Node);
            CacheRecency.AddLast(cached.Node);
            return cached.Image;
        }
    }

    private static void AddCachedTile(string key, BitmapSource image)
    {
        lock (CacheLock)
        {
            if (Cache.Remove(key, out var previous))
            {
                CacheRecency.Remove(previous.Node);
            }

            var node = CacheRecency.AddLast(key);
            Cache[key] = new CachedTile(image, node);
            while (Cache.Count > MaximumCachedTiles)
            {
                var oldest = CacheRecency.First!;
                CacheRecency.RemoveFirst();
                Cache.Remove(oldest.Value);
            }
        }
    }

    private sealed record CachedTile(BitmapSource Image, LinkedListNode<string> Node);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragOrigin = e.GetPosition(this);
        _dragLatitude = CenterLatitude;
        _dragLongitude = CenterLongitude;
        _dragging = true;
        _moved = false;
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
        var zoom = Math.Clamp(Zoom, 3, 12);
        var deltaX = position.X - _dragOrigin.X;
        var deltaY = position.Y - _dragOrigin.Y;
        if (Math.Abs(deltaX) + Math.Abs(deltaY) > 3)
        {
            _moved = true;
        }

        var center = LatLonToWorld(_dragLatitude, _dragLongitude, zoom);
        var (latitude, longitude) = WorldToLatLon(new Point(center.X - deltaX, center.Y - deltaY), zoom);
        CenterLatitude = Math.Clamp(latitude, -85, 85);
        CenterLongitude = Math.Clamp(longitude, -180, 180);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        if (!_moved)
        {
            var zoom = Math.Clamp(Zoom, 3, 12);
            var center = LatLonToWorld(CenterLatitude, CenterLongitude, zoom);
            var originX = center.X - ActualWidth / 2;
            var originY = center.Y - ActualHeight / 2;
            var position = e.GetPosition(this);
            var (latitude, longitude) = WorldToLatLon(new Point(originX + position.X, originY + position.Y), zoom);
            MarkerLatitude = Math.Clamp(latitude, -85, 85);
            MarkerLongitude = Math.Clamp(longitude, -180, 180);
        }

        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Zoom = Math.Clamp(Zoom + (e.Delta > 0 ? 1 : -1), 3, 12);
        e.Handled = true;
    }

    private static Point LatLonToWorld(double latitude, double longitude, int zoom)
    {
        var scale = TileSize * (double)(1 << zoom);
        var x = (longitude + 180.0) / 360.0 * scale;
        var sinLatitude = Math.Sin(Math.Clamp(latitude, -85, 85) * Math.PI / 180.0);
        var y = (0.5 - Math.Log((1 + sinLatitude) / (1 - sinLatitude)) / (4 * Math.PI)) * scale;
        return new Point(x, y);
    }

    private static (double Latitude, double Longitude) WorldToLatLon(Point world, int zoom)
    {
        var scale = TileSize * (double)(1 << zoom);
        var longitude = world.X / scale * 360.0 - 180.0;
        var n = Math.PI - 2 * Math.PI * world.Y / scale;
        var latitude = 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        return (latitude, longitude);
    }
}
