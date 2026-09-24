namespace HuePC.Core.Services;

public static class BleReconnectDelayPolicy
{
    private static readonly int[] DelaySeconds = [1, 2, 4, 8, 16, 30];

    public static TimeSpan ForAttempt(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        return TimeSpan.FromSeconds(DelaySeconds[Math.Min(attempt - 1, DelaySeconds.Length - 1)]);
    }
}
