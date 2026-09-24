using HuePC.Core.Models;

namespace HuePC.Core.Services;

/// <summary>
/// Turns a block of mono samples into a feature frame using a Hann window and a radix-2 FFT.
/// Stateful because spectral flux (used for beat detection) compares the current magnitude
/// spectrum with the previous one. Pure math, so it can be unit tested without audio hardware.
/// </summary>
public sealed class AudioSpectrumAnalyzer
{
    public const int FftSize = 2048;
    private const double BassUpperHz = 250;
    private const double MidUpperHz = 2000;
    private const double TrebleUpperHz = 12000;
    private const double MinFrequencyHz = 30;

    private readonly float[] _previousMagnitudes = new float[FftSize / 2];

    public void Reset() => Array.Clear(_previousMagnitudes);

    public AudioSpectrumFrame Analyze(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0)
        {
            return new AudioSpectrumFrame(0, 0, 0, 0);
        }

        var size = FftSize;
        var real = new float[size];
        var imag = new float[size];
        var offset = Math.Max(0, samples.Length - size);
        var count = Math.Min(size, samples.Length);

        double squareSum = 0;
        for (var index = 0; index < count; index++)
        {
            var sample = samples[offset + index];
            squareSum += sample * sample;
            var window = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * index / (count - 1 <= 0 ? 1 : count - 1));
            real[index] = sample * window;
        }

        Fft(real, imag);

        var magnitudes = new double[size / 2];
        double bass = 0, mid = 0, treble = 0;
        int bassBins = 0, midBins = 0, trebleBins = 0;
        double weightedFrequency = 0, magnitudeSum = 0, logSum = 0;
        int logBins = 0;

        for (var bin = 1; bin < size / 2; bin++)
        {
            var frequency = (double)bin * sampleRate / size;
            if (frequency < MinFrequencyHz)
            {
                continue;
            }

            var magnitude = Math.Sqrt(real[bin] * real[bin] + imag[bin] * imag[bin]) / size;
            magnitudes[bin] = magnitude;

            if (frequency < BassUpperHz) { bass += magnitude; bassBins++; }
            else if (frequency < MidUpperHz) { mid += magnitude; midBins++; }
            else if (frequency < TrebleUpperHz) { treble += magnitude; trebleBins++; }

            if (frequency < TrebleUpperHz)
            {
                weightedFrequency += frequency * magnitude;
                magnitudeSum += magnitude;
                logSum += Math.Log(magnitude + 1e-12);
                logBins++;
            }
        }

        var centroid = magnitudeSum > 0 ? weightedFrequency / magnitudeSum : 0;
        var arithmeticMean = logBins > 0 ? magnitudeSum / logBins : 0;
        var geometricMean = logBins > 0 ? Math.Exp(logSum / logBins) : 0;
        var flatness = arithmeticMean > 0 ? Math.Clamp(geometricMean / arithmeticMean, 0, 1) : 0;

        double flux = 0, lowFlux = 0;
        for (var bin = 1; bin < size / 2; bin++)
        {
            var frequency = (double)bin * sampleRate / size;
            var difference = magnitudes[bin] - _previousMagnitudes[bin];
            if (difference > 0)
            {
                flux += difference;
                if (frequency < BassUpperHz)
                {
                    lowFlux += difference;
                }
            }

            _previousMagnitudes[bin] = (float)magnitudes[bin];
        }

        var chroma = ComputeChroma(magnitudes, sampleRate);
        var level = Math.Sqrt(squareSum / count);

        return new AudioSpectrumFrame(
            bassBins == 0 ? 0 : bass / bassBins,
            midBins == 0 ? 0 : mid / midBins,
            trebleBins == 0 ? 0 : treble / trebleBins,
            level,
            centroid,
            flatness,
            lowFlux / Math.Max(1, bassBins),
            flux / Math.Max(1, size / 2),
            chroma);
    }

    private static double[] ComputeChroma(double[] magnitudes, int sampleRate)
    {
        var chroma = new double[12];
        var size = FftSize;
        for (var bin = 2; bin < size / 2; bin++)
        {
            var frequency = (double)bin * sampleRate / size;
            if (frequency is < 65 or > 2000)
            {
                continue;
            }

            var magnitude = magnitudes[bin];
            if (bin > 2 && magnitude < magnitudes[bin - 1] && magnitude < magnitudes[bin + 1])
            {
                continue;
            }

            var midi = 69 + 12 * Math.Log2(frequency / 440.0);
            var pitchClass = ((int)Math.Round(midi) % 12 + 12) % 12;
            chroma[pitchClass] += magnitude;
        }

        var maximum = chroma.Max();
        if (maximum > 0)
        {
            for (var index = 0; index < 12; index++)
            {
                chroma[index] /= maximum;
            }
        }

        return chroma;
    }

    private static void Fft(float[] real, float[] imag)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var wReal = Math.Cos(angle);
            var wImag = Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                double curReal = 1, curImag = 0;
                for (var j = 0; j < length / 2; j++)
                {
                    var evenIndex = i + j;
                    var oddIndex = i + j + length / 2;
                    var oddReal = real[oddIndex] * curReal - imag[oddIndex] * curImag;
                    var oddImag = real[oddIndex] * curImag + imag[oddIndex] * curReal;
                    real[oddIndex] = (float)(real[evenIndex] - oddReal);
                    imag[oddIndex] = (float)(imag[evenIndex] - oddImag);
                    real[evenIndex] = (float)(real[evenIndex] + oddReal);
                    imag[evenIndex] = (float)(imag[evenIndex] + oddImag);

                    var nextReal = curReal * wReal - curImag * wImag;
                    curImag = curReal * wImag + curImag * wReal;
                    curReal = nextReal;
                }
            }
        }
    }
}
