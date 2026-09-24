using HuePC.Core.Models;

namespace HuePC.Core.Services;

/// <summary>
/// Normalizes bands and level against a slowly decaying running average instead of the maximum
/// seen so far. Relative differences between sections (verse vs chorus) survive, which the old
/// maximum based version flattened out.
/// </summary>
public sealed class AdaptiveSpectrumNormalizer
{
    private const double Decay = 0.992;
    private const double Headroom = 1.5;
    private const double Floor = 1e-5;

    private double _bassAverage = Floor;
    private double _midAverage = Floor;
    private double _trebleAverage = Floor;
    private double _levelAverage = Floor;

    public AudioSpectrumFrame Normalize(AudioSpectrumFrame frame)
    {
        _bassAverage = Math.Max(Floor, _bassAverage * Decay + frame.Bass * (1 - Decay));
        _midAverage = Math.Max(Floor, _midAverage * Decay + frame.Mid * (1 - Decay));
        _trebleAverage = Math.Max(Floor, _trebleAverage * Decay + frame.Treble * (1 - Decay));
        _levelAverage = Math.Max(Floor, _levelAverage * Decay + frame.Level * (1 - Decay));

        return frame with
        {
            Bass = Scale(frame.Bass, _bassAverage),
            Mid = Scale(frame.Mid, _midAverage),
            Treble = Scale(frame.Treble, _trebleAverage),
            Level = Scale(frame.Level, _levelAverage)
        };
    }

    public void Reset()
    {
        _bassAverage = _midAverage = _trebleAverage = _levelAverage = Floor;
    }

    private static double Scale(double value, double average) =>
        Math.Clamp(value / (average * Headroom), 0, 1);
}
