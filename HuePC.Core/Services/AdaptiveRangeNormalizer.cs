namespace HuePC.Core.Services;

/// <summary>
/// Adapts a low/high range to the incoming values so a feature that only moves inside a narrow
/// band (for example the spectral centroid of a song) still maps across the full 0..1 output.
/// The range expands immediately and drifts back slowly, so it follows the current song without
/// jumping around between tracks.
/// </summary>
public sealed class AdaptiveRangeNormalizer
{
    private const double Drift = 0.0006;

    public AdaptiveRangeNormalizer(double low, double high, double minimumSpan = 1)
    {
        Low = low;
        High = high;
        MinimumSpan = minimumSpan;
    }

    public double Low { get; private set; }
    public double High { get; private set; }
    public double MinimumSpan { get; }

    public double Normalize(double value)
    {
        if (value < Low)
        {
            Low = value;
        }
        else
        {
            Low += (value - Low) * Drift;
        }

        if (value > High)
        {
            High = value;
        }
        else
        {
            High += (value - High) * Drift;
        }

        if (High - Low < MinimumSpan)
        {
            High = Low + MinimumSpan;
        }

        return Math.Clamp((value - Low) / (High - Low), 0, 1);
    }

    public void Reset(double low, double high)
    {
        Low = low;
        High = high;
    }
}
