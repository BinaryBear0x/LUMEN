using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HuePC.Core.Services;
using Timer = System.Threading.Timer;

namespace HuePC.SystemIntegration;

public sealed record ScreenSample(HueRgb Color, double Luminance);

/// <summary>
/// Captures the primary screen a few times per second and reports the average colour by sampling
/// a sparse grid, which keeps the CPU cost low enough to run alongside the light control.
/// </summary>
public sealed class ScreenColorSampler : IDisposable
{
    private const int HalftoneStretchMode = 4;
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureLayeredWindows = 0x40000000;
    private readonly int _intervalMilliseconds;
    private Timer? _timer;
    private bool _disposed;
    private int _sampling;

    public ScreenColorSampler(int framesPerSecond = 4)
    {
        _intervalMilliseconds = Math.Max(100, 1000 / Math.Clamp(framesPerSecond, 1, 10));
    }

    public event EventHandler<ScreenSample>? Sampled;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer ??= new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(_intervalMilliseconds));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0)
        {
            return;
        }

        try
        {
            var screen = Screen.PrimaryScreen;
            if (screen is null)
            {
                return;
            }

            var bounds = screen.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            var step = Math.Max(16, Math.Min(bounds.Width, bounds.Height) / 24);
            var sampleWidth = Math.Max(1, bounds.Width / step);
            var sampleHeight = Math.Max(1, bounds.Height / step);
            using var bitmap = new Bitmap(sampleWidth, sampleHeight, PixelFormat.Format32bppArgb);
            var screenHdc = GetDC(IntPtr.Zero);
            if (screenHdc == IntPtr.Zero)
            {
                return;
            }

            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                var destinationHdc = graphics.GetHdc();
                try
                {
                    SetStretchBltMode(destinationHdc, HalftoneStretchMode);
                    SetBrushOrgEx(destinationHdc, 0, 0, IntPtr.Zero);
                    if (!StretchBlt(
                            destinationHdc,
                            0,
                            0,
                            sampleWidth,
                            sampleHeight,
                            screenHdc,
                            bounds.Left,
                            bounds.Top,
                            bounds.Width,
                            bounds.Height,
                            SourceCopy | CaptureLayeredWindows))
                    {
                        return;
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(destinationHdc);
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenHdc);
            }

            var pixelData = bitmap.LockBits(
                new Rectangle(0, 0, sampleWidth, sampleHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            long red = 0, green = 0, blue = 0;
            var count = sampleWidth * sampleHeight;
            try
            {
                var row = new byte[Math.Abs(pixelData.Stride)];
                for (var y = 0; y < sampleHeight; y++)
                {
                    Marshal.Copy(IntPtr.Add(pixelData.Scan0, y * pixelData.Stride), row, 0, row.Length);
                    for (var x = 0; x < sampleWidth; x++)
                    {
                        var offset = x * 4;
                        blue += row[offset];
                        green += row[offset + 1];
                        red += row[offset + 2];
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(pixelData);
            }

            if (count == 0)
            {
                return;
            }

            var color = new HueRgb((byte)(red / count), (byte)(green / count), (byte)(blue / count));
            var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
            Sampled?.Invoke(this, new ScreenSample(color, luminance));
        }
        catch
        {
            // Screen capture can fail for a frame (locked session, fullscreen exclusive); skip it.
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int SetStretchBltMode(IntPtr hDc, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(IntPtr hDc, int x, int y, IntPtr previousOrigin);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(
        IntPtr destinationHdc,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        IntPtr sourceHdc,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        uint rasterOperation);
}
