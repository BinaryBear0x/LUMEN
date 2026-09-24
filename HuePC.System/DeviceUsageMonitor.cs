using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace HuePC.SystemIntegration;

/// <summary>
/// Detects whether the microphone or camera is currently in use by reading the Windows capability
/// consent store (the same data behind the taskbar privacy indicators). The store records a start
/// time per app and a stop time of zero while the device is in use.
/// </summary>
public sealed class DeviceUsageMonitor : IDisposable
{
    private const string ConsentPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private Timer? _timer;
    private bool _lastMicrophone;
    private bool _lastCamera;
    private bool _disposed;

    public event EventHandler<(bool Microphone, bool Camera)>? UsageChanged;

    public bool MicrophoneInUse { get; private set; }
    public bool CameraInUse { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Poll()
    {
        try
        {
            MicrophoneInUse = IsCapabilityInUse("microphone");
            CameraInUse = IsCapabilityInUse("webcam");
            if (MicrophoneInUse != _lastMicrophone || CameraInUse != _lastCamera)
            {
                _lastMicrophone = MicrophoneInUse;
                _lastCamera = CameraInUse;
                UsageChanged?.Invoke(this, (MicrophoneInUse, CameraInUse));
            }
        }
        catch
        {
            // Best effort; the registry may be unavailable for a moment.
        }
    }

    private static bool IsCapabilityInUse(string capability)
    {
        using var root = Registry.CurrentUser.OpenSubKey($@"{ConsentPath}\{capability}");
        if (root is null)
        {
            return false;
        }

        foreach (var keyName in root.GetSubKeyNames())
        {
            using var appKey = root.OpenSubKey(keyName);
            if (appKey is null)
            {
                continue;
            }

            if (IsKeyInUse(appKey))
            {
                return true;
            }

            if (!string.Equals(keyName, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var exeName in appKey.GetSubKeyNames())
            {
                using var exeKey = appKey.OpenSubKey(exeName);
                if (exeKey is not null && IsKeyInUse(exeKey))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsKeyInUse(RegistryKey key)
    {
        if (key.GetValue("LastUsedTimeStart") is not long start || start <= 0)
        {
            return false;
        }

        var stop = key.GetValue("LastUsedTimeStop") is long stopValue ? stopValue : 0;
        return stop == 0 || stop < start;
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
}
