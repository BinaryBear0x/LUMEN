using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuePC.Audio;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.App.ViewModels;

public sealed partial class MusicModeOptionViewModel : ObservableObject
{
    public MusicModeOptionViewModel(MusicLightMode mode, string name)
    {
        Mode = mode;
        Name = name;
    }

    public MusicLightMode Mode { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isActive;
}

public sealed partial class MainWindowViewModel
{
    private readonly SystemAudioSpectrumSource _spectrumSource = new();
    private readonly AdaptiveSpectrumNormalizer _spectrumNormalizer = new();
    private readonly AdaptiveRangeNormalizer _centroidRange = new(800, 6000, 200);
    private readonly BeatDetector _beatDetector = new();
    private volatile AudioSpectrumFrame? _latestSpectrum;
    private CancellationTokenSource? _musicCts;
    private bool _musicWriteBusy;
    private DateTimeOffset _lastMusicUiUpdate;
    private double _smoothedHue = -1;
    private double _beatEnvelope;
    private int _lastColorR = -1;
    private int _lastColorG = -1;
    private int _lastColorB = -1;
    private int _lastBrightness = -1;

    [ObservableProperty] private bool _isMusicModeRunning;
    [ObservableProperty] private double _musicSensitivity = 60;
    [ObservableProperty] private double _musicDynamics = 45;
    [ObservableProperty] private double _musicLevel;
    [ObservableProperty] private string _musicStatusText = "Durduruldu";
    [ObservableProperty] private MusicModeOptionViewModel? _musicMode;
    [ObservableProperty] private double _musicBass;
    [ObservableProperty] private double _musicMid;
    [ObservableProperty] private double _musicTreble;
    [ObservableProperty] private double _musicBeatPulse;
    [ObservableProperty] private IReadOnlyList<double>? _musicChroma;
    [ObservableProperty] private string _musicCalibrationText = "Kalibrasyon: hazır";

    public IReadOnlyList<MusicModeOptionViewModel> MusicModes { get; } =
    [
        new(MusicLightMode.Beat, "Vuruş"),
        new(MusicLightMode.Calm, "Sakin"),
        new(MusicLightMode.Synesthesia, "Sinestezi")
    ];

    public string MusicSensitivityLabel => $"{Math.Round(MusicSensitivity):0}%";
    public string MusicDynamicsLabel => $"{Math.Round(MusicDynamics):0}%";

    [RelayCommand]
    private void SelectMusicMode(MusicModeOptionViewModel? option)
    {
        if (option is null) return;
        MusicMode = option;
    }

    [RelayCommand(CanExecute = nameof(CanControlLight))]
    private async Task ToggleMusicModeAsync()
    {
        if (IsMusicModeRunning)
        {
            StopMusicMode();
            StatusMessage = "Müzik modu durduruldu.";
            return;
        }

        var connection = GetSelectedConnection();
        if (connection is null) return;

        StopProfile();
        await connection.SetEffectAsync(HueEffect.None, 128);
        SetActiveEffect(null);

        _spectrumNormalizer.Reset();
        _beatDetector.Reset();
        _centroidRange.Reset(800, 6000);
        _latestSpectrum = null;
        _smoothedHue = -1;
        _beatEnvelope = 0;
        _lastColorR = _lastColorG = _lastColorB = -1;
        _lastBrightness = -1;

        try
        {
            _spectrumSource.Start();
        }
        catch (Exception exception)
        {
            StatusMessage = "Ses çıkışı yakalanamadı. Varsayılan bir hoparlör/kulaklık seçili mi?";
            await LogAsync("Error", "Ses yakalama başlatılamadı.", ErrorData(exception));
            return;
        }

        _spectrumSource.FrameAvailable -= OnSpectrumFrame;
        _spectrumSource.FrameAvailable += OnSpectrumFrame;

        _musicCts = new CancellationTokenSource();
        IsMusicModeRunning = true;
        MusicStatusText = $"{MusicMode?.Name ?? "Vuruş"} · dinliyor";
        _ = RunMusicLoopAsync(connection, _musicCts.Token);
        StatusMessage = "Müzik modu başlatıldı: ışık sesin spektrumuna göre değişiyor.";
        await LogAsync("Information", "Müzik modu başlatıldı.", new { Mode = MusicMode?.Name, MusicSensitivity, MusicDynamics });
    }

    private void OnSpectrumFrame(object? sender, AudioSpectrumFrame frame)
    {
        var normalized = _spectrumNormalizer.Normalize(frame);
        _latestSpectrum = normalized;

        // Feed the live visualiser at a modest rate; the analyser produces frames far faster.
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastMusicUiUpdate).TotalMilliseconds < 60) return;
        _lastMusicUiUpdate = now;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            MusicBass = Math.Clamp(normalized.Bass, 0, 1);
            MusicMid = Math.Clamp(normalized.Mid, 0, 1);
            MusicTreble = Math.Clamp(normalized.Treble, 0, 1);
            MusicBeatPulse = _beatEnvelope;
            MusicChroma = normalized.Chroma;
        }));
    }

    /// <summary>Samples the live spectrum for six seconds and picks a sensitivity that suits the room.</summary>
    [RelayCommand]
    private async Task CalibrateMusicAsync()
    {
        if (!IsMusicModeRunning)
        {
            MusicCalibrationText = "Önce müzik modunu başlatın.";
            StatusMessage = "Kalibrasyon için müzik modu çalışmalı.";
            return;
        }

        MusicCalibrationText = "Kalibrasyon: 6 saniye dinleniyor…";
        var samples = new List<double>();
        var end = DateTimeOffset.UtcNow.AddSeconds(6);
        while (DateTimeOffset.UtcNow < end)
        {
            var frame = _latestSpectrum;
            if (frame is not null)
            {
                samples.Add(Math.Clamp(frame.Level, 0, 1));
            }

            await Task.Delay(120);
        }

        if (samples.Count == 0)
        {
            MusicCalibrationText = "Kalibrasyon: ses yakalanamadı.";
            return;
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var sensitivity = Math.Clamp(Math.Round(100 - median * 90), 20, 90);
        MusicSensitivity = sensitivity;
        MusicCalibrationText = $"Kalibrasyon: %{sensitivity:0} (ort. seviye %{median * 100:0})";
        StatusMessage = $"Hassasiyet %{sensitivity:0} olarak ayarlandı.";
        await LogAsync("Information", "Müzik hassasiyeti kalibre edildi.", new { MedianLevel = median, Sensitivity = sensitivity });
    }

    private async Task RunMusicLoopAsync(IBleConnection connection, CancellationToken cancellationToken)
    {
        var lastSend = DateTimeOffset.MinValue;
        var lastFrameAt = DateTimeOffset.UtcNow;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = _latestSpectrum;
                var now = DateTimeOffset.UtcNow;
                if (frame is not null)
                {
                    var mode = MusicMode?.Mode ?? MusicLightMode.Beat;
                    var beat = _beatDetector.Update(frame.LowFlux, now);
                    var elapsed = Math.Max(0.016, (now - lastFrameAt).TotalSeconds);
                    lastFrameAt = now;
                    _beatEnvelope = beat.IsBeat ? 1 : Math.Max(0, _beatEnvelope - elapsed / 0.45);

                    var targetHue = MusicLightMapper.GetHue(frame, mode, _centroidRange.Normalize(frame.Centroid));
                    if (_smoothedHue < 0)
                    {
                        _smoothedHue = targetHue;
                    }
                    else
                    {
                        var difference = (targetHue - _smoothedHue + 540) % 360 - 180;
                        _smoothedHue = (_smoothedHue + difference * (mode == MusicLightMode.Beat ? 0.5 : 0.3) + 360) % 360;
                    }

                    var intensity = Math.Clamp(MusicSensitivity / 100.0, 0, 1);
                    var value = Math.Clamp(MusicLightMapper.GetValue(frame.Level, MusicDynamics) * (0.35 + 0.65 * intensity), 0.05, 1);
                    var color = MusicLightMapper.ToColor(_smoothedHue, MusicLightMapper.GetSaturation(frame, mode), value);
                    var boost = _beatEnvelope * (mode == MusicLightMode.Beat ? 80 : 30) * intensity;
                    var brightness = (byte)Math.Clamp(30 + value * 224 + boost, 30, 254);

                    var changed = Math.Abs(color.R - _lastColorR) + Math.Abs(color.G - _lastColorG) + Math.Abs(color.B - _lastColorB) > 14
                        || Math.Abs(brightness - _lastBrightness) > 4;
                    if ((changed || beat.IsBeat) && !_musicWriteBusy &&
                        now - lastSend >= TimeSpan.FromMilliseconds(beat.IsBeat ? 80 : 130))
                    {
                        _musicWriteBusy = true;
                        try
                        {
                            var (x, y) = HueColorConverter.ToXy(color.R, color.G, color.B);
                            var result = await connection.SetColorAndBrightnessAsync(x, y, brightness, cancellationToken);
                            if (result.IsSuccess)
                            {
                                _lastColorR = color.R;
                                _lastColorG = color.G;
                                _lastColorB = color.B;
                                _lastBrightness = brightness;
                                MusicLevel = Math.Round(frame.Level * 100);
                            }

                            lastSend = DateTimeOffset.UtcNow;
                        }
                        finally
                        {
                            _musicWriteBusy = false;
                        }
                    }
                }

                await Task.Delay(30, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await LogAsync("Error", "Müzik modu döngüsünde hata oluştu.", ErrorData(exception));
        }
        finally
        {
            IsMusicModeRunning = false;
            MusicStatusText = "Durduruldu";
            MusicLevel = 0;
            MusicBass = 0;
            MusicMid = 0;
            MusicTreble = 0;
            MusicBeatPulse = 0;
            MusicChroma = null;
        }
    }

    public void StopMusicMode()
    {
        if (_musicCts is not null)
        {
            _musicCts.Cancel();
            _musicCts.Dispose();
            _musicCts = null;
        }

        try
        {
            _spectrumSource.FrameAvailable -= OnSpectrumFrame;
            _spectrumSource.Stop();
        }
        catch
        {
            // Stopping a disposed capture must never surface.
        }

        _beatDetector.Reset();
        _smoothedHue = -1;
        _beatEnvelope = 0;
        IsMusicModeRunning = false;
        MusicStatusText = "Durduruldu";
        MusicLevel = 0;
        MusicBass = 0;
        MusicMid = 0;
        MusicTreble = 0;
        MusicBeatPulse = 0;
        MusicChroma = null;
    }

    partial void OnMusicSensitivityChanged(double value) => OnPropertyChanged(nameof(MusicSensitivityLabel));

    partial void OnMusicDynamicsChanged(double value) => OnPropertyChanged(nameof(MusicDynamicsLabel));

    partial void OnMusicModeChanged(MusicModeOptionViewModel? value)
    {
        foreach (var option in MusicModes)
        {
            option.IsActive = ReferenceEquals(option, value);
        }

        MusicStatusText = IsMusicModeRunning ? $"{value?.Name ?? "Vuruş"} · dinliyor" : "Durduruldu";
    }
}
