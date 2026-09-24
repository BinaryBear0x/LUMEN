namespace HuePC.Core.Services;

public readonly record struct BeatInfo(bool IsBeat, double Strength);

/// <summary>
/// Detects beats from the low band spectral flux. Keeps an adaptive threshold based on the
/// running mean and deviation of the flux so quiet and loud sections both work, and enforces a
/// minimum interval so a single hit cannot fire twice.
/// </summary>
public sealed class BeatDetector
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(180);
    private const double ThresholdFactor = 1.6;
    private const double MinimumFlux = 1e-5;
    private const int WarmupFrames = 25;

    private double _mean;
    private double _variance;
    private int _frames;
    private DateTimeOffset _lastBeat = DateTimeOffset.MinValue;

    public void Reset()
    {
        _mean = 0;
        _variance = 0;
        _frames = 0;
        _lastBeat = DateTimeOffset.MinValue;
    }

    public BeatInfo Update(double lowFlux, DateTimeOffset now)
    {
        _frames++;
        var deviation = Math.Sqrt(Math.Max(0, _variance));
        var threshold = _mean + ThresholdFactor * deviation;
        var isBeat = false;
        var strength = 0.0;

        if (_frames > WarmupFrames &&
            lowFlux > MinimumFlux &&
            lowFlux > threshold &&
            now - _lastBeat >= MinimumInterval)
        {
            isBeat = true;
            strength = Math.Clamp(lowFlux / (_mean + 1e-9), 1, 6) / 6.0;
            _lastBeat = now;
        }

        var delta = lowFlux - _mean;
        _mean += delta / 12.0;
        _variance += (delta * (lowFlux - _mean) - _variance) / 12.0;
        return new BeatInfo(isBeat, strength);
    }
}
