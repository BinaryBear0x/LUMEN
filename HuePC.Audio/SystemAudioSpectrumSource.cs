using HuePC.Core.Models;
using HuePC.Core.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HuePC.Audio;

/// <summary>
/// Captures the default Windows playback device (WASAPI loopback) and raises an analysed
/// spectrum frame roughly ten times per second. Requires audio to be playing on the machine.
/// </summary>
public sealed class SystemAudioSpectrumSource : IDisposable
{
    private static readonly TimeSpan AnalysisInterval = TimeSpan.FromMilliseconds(60);
    private readonly AudioSpectrumAnalyzer _analyzer = new();
    private WasapiRecorder? _recorder;
    private DateTimeOffset _lastAnalysis;
    private bool _disposed;

    public event EventHandler<AudioSpectrumFrame>? FrameAvailable;

    public bool IsRunning => _recorder is not null;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recorder is not null)
        {
            return;
        }

        var recorder = new WasapiRecorderBuilder()
            .WithLoopbackCapture()
            .WithPollingSync()
            .Build();
        recorder.DataAvailable += OnDataAvailable;
        _analyzer.Reset();
        _recorder = recorder;
        recorder.StartRecording();
    }

    public void Stop()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null)
        {
            return;
        }

        recorder.DataAvailable -= OnDataAvailable;
        try
        {
            recorder.StopRecording();
        }
        catch
        {
            // The device may already be gone; nothing to do.
        }

        recorder.Dispose();
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_disposed || _recorder is null || buffer.IsEmpty)
        {
            return;
        }

        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastAnalysis < AnalysisInterval)
        {
            return;
        }

        _lastAnalysis = now;
        var format = _recorder.WaveFormat;
        if (format is WaveFormatExtensible extensible)
        {
            // WASAPI loopback reports WAVE_FORMAT_EXTENSIBLE; normalise it so the sample
            // conversion below can rely on the plain float/PCM encodings.
            format = extensible.ToStandardWaveFormat();
        }

        var samples = ConvertToMono(buffer, format);
        if (samples.Length == 0)
        {
            return;
        }

        var frame = _analyzer.Analyze(samples, format.SampleRate);
        FrameAvailable?.Invoke(this, frame);
    }

    private static float[] ConvertToMono(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var frames = buffer.Length / 4 / channels;
            var result = new float[frames];
            for (var frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToSingle(buffer.Slice((frame * channels + channel) * 4, 4));
                }

                result[frame] = sum / channels;
            }

            return result;
        }

        if (format.BitsPerSample == 16)
        {
            var frames = buffer.Length / 2 / channels;
            var result = new float[frames];
            for (var frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToInt16(buffer.Slice((frame * channels + channel) * 2, 2)) / 32768f;
                }

                result[frame] = sum / channels;
            }

            return result;
        }

        return [];
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
