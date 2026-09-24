using System.Net.NetworkInformation;
using System.Windows.Forms;
using Timer = System.Threading.Timer;

namespace HuePC.SystemIntegration;

/// <summary>
/// Polls battery, power line and network availability and raises only transitions:
/// charger plugged in or unplugged, battery dropping below the alert threshold and the network
/// becoming available or unavailable.
/// </summary>
public sealed class SystemEventMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatteryAlertPercent = 20;
    private Timer? _timer;
    private bool _lastOnline = true;
    private bool _lastAc = true;
    private bool _lowNotified;
    private bool _hasBattery;
    private bool _disposed;

    public event EventHandler<bool>? ChargerChanged;
    public event EventHandler<int>? BatteryLow;
    public event EventHandler<bool>? NetworkChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timer is not null)
        {
            return;
        }

        Prime();
        _timer = new Timer(_ => Poll(), null, PollInterval, PollInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Prime()
    {
        try
        {
            _lastOnline = NetworkInterface.GetIsNetworkAvailable();
            var power = SystemInformation.PowerStatus;
            _hasBattery = power.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
            _lastAc = power.PowerLineStatus != PowerLineStatus.Offline;
        }
        catch
        {
            _hasBattery = false;
        }
    }

    private void Poll()
    {
        try
        {
            var power = SystemInformation.PowerStatus;
            var ac = power.PowerLineStatus != PowerLineStatus.Offline;
            if (ac != _lastAc)
            {
                _lastAc = ac;
                ChargerChanged?.Invoke(this, ac);
            }

            if (_hasBattery && !ac)
            {
                var percent = (int)Math.Round(Math.Clamp(power.BatteryLifePercent, 0f, 1f) * 100);
                if (percent <= BatteryAlertPercent && !_lowNotified)
                {
                    _lowNotified = true;
                    BatteryLow?.Invoke(this, percent);
                }
                else if (percent > BatteryAlertPercent + 5)
                {
                    _lowNotified = false;
                }
            }

            var online = NetworkInterface.GetIsNetworkAvailable();
            if (online != _lastOnline)
            {
                _lastOnline = online;
                NetworkChanged?.Invoke(this, online);
            }
        }
        catch
        {
            // Best effort.
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
}
