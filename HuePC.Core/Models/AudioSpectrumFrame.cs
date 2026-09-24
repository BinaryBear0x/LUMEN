namespace HuePC.Core.Models;

/// <summary>
/// One audio analysis frame. Band values are average magnitudes, <see cref="Level"/> is the RMS
/// of the analysed samples and the remaining fields are spectral features used by the light
/// mapper: <see cref="Centroid"/> in Hz, <see cref="Flatness"/> 0..1 (1 = noise like),
/// spectral flux values and a 12 element chroma vector.
/// </summary>
public sealed record AudioSpectrumFrame(
    double Bass,
    double Mid,
    double Treble,
    double Level,
    double Centroid = 0,
    double Flatness = 0,
    double LowFlux = 0,
    double Flux = 0,
    IReadOnlyList<double>? Chroma = null);
