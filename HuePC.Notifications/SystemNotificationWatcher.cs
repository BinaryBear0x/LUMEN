using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace HuePC.Notifications;

public sealed record NotificationEvent(uint Id, string AppName, string Title, string Body, DateTimeOffset ReceivedAt);

/// <summary>
/// Watches Windows toast notifications. The change event is not available to unpackaged desktop
/// apps (COMException 0x80070490), so the notification list is polled every two seconds and new
/// ids are reported. Existing notifications at startup are ignored. When a notification disappears
/// from the list (dismissed or read in the Action Center), <see cref="NotificationRemoved"/> is
/// raised with the stored details so callers can restore the light.
/// </summary>
public sealed class SystemNotificationWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly Dictionary<uint, NotificationEvent> _known = [];
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cancellation;
    private bool _primed;
    private bool _disposed;

    public event EventHandler<NotificationEvent>? NotificationAdded;
    public event EventHandler<NotificationEvent>? NotificationRemoved;

    public bool IsRunning => _cancellation is not null;

    public static async Task<bool> EnsureAccessAsync()
    {
        try
        {
            var status = await UserNotificationListener.Current.RequestAccessAsync();
            return status == UserNotificationListenerAccessStatus.Allowed;
        }
        catch
        {
            return false;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cancellation is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        lock (_stateLock)
        {
            _known.Clear();
            _primed = false;
        }
        _ = PollAsync(_cancellation.Token);
    }

    public void Stop()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        cancellation?.Cancel();
        lock (_stateLock)
        {
            _known.Clear();
            _primed = false;
        }

        cancellation?.Dispose();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var notifications = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var currentIds = new HashSet<uint>(notifications.Count);
                lock (_stateLock)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    foreach (var notification in notifications)
                    {
                        currentIds.Add(notification.Id);
                        if (_known.ContainsKey(notification.Id))
                        {
                            continue;
                        }

                        var notificationEvent = CreateEvent(notification);
                        _known[notification.Id] = notificationEvent;
                        if (_primed)
                        {
                            NotificationAdded?.Invoke(this, notificationEvent);
                        }
                    }

                    if (_known.Count > 0)
                    {
                        foreach (var removed in _known.Keys.Where(id => !currentIds.Contains(id)).ToArray())
                        {
                            var notificationEvent = _known[removed];
                            _known.Remove(removed);
                            NotificationRemoved?.Invoke(this, notificationEvent);
                        }
                    }

                    _primed = true;
                }
            }
            catch
            {
                // The listener is best effort; keep polling.
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static NotificationEvent CreateEvent(UserNotification notification)
    {
        var appName = notification.AppInfo?.DisplayInfo?.DisplayName
            ?? notification.AppInfo?.AppUserModelId
            ?? "Bilinmeyen uygulama";
        var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements()?
            .Select(element => element.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray() ?? [];
        return new NotificationEvent(
            notification.Id,
            appName,
            texts.Length > 0 ? texts[0] : string.Empty,
            texts.Length > 1 ? texts[1] : string.Empty,
            DateTimeOffset.Now);
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
