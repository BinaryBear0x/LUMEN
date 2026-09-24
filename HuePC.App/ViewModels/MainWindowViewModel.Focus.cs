using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuePC.Core.Services;

namespace HuePC.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly HueRgb PomodoroBreakColor = new(0x2E, 0xE6, 0x5B);
    private const ushort PomodoroFocusMired = 250;

    private readonly DispatcherTimer _pomodoroTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TimeSpan _pomodoroRemaining;
    private bool _pomodoroIsWorkPhase = true;

    [ObservableProperty] private bool _isPomodoroRunning;
    [ObservableProperty] private string _pomodoroPhaseText = "Hazır";
    [ObservableProperty] private string _pomodoroTimeText = "25:00";
    [ObservableProperty] private int _pomodoroCompletedSessions;
    [ObservableProperty] private double _pomodoroWorkMinutes = 25;
    [ObservableProperty] private double _pomodoroBreakMinutes = 5;

    public string PomodoroCycleText => $"{PomodoroCompletedSessions} tur tamamlandı";
    public string PomodoroSummary => IsPomodoroRunning
        ? $"{PomodoroPhaseText} · {PomodoroTimeText} kaldı"
        : "Hazır";

    [RelayCommand]
    private void TogglePomodoro()
    {
        if (IsPomodoroRunning)
        {
            StopPomodoro();
        }
        else
        {
            _ = StartPomodoroAsync();
        }
    }

    private async Task StartPomodoroAsync()
    {
        if (_disposed) return;
        _pomodoroTimer.Tick -= OnPomodoroTick;
        _pomodoroTimer.Tick += OnPomodoroTick;
        _pomodoroIsWorkPhase = true;
        _pomodoroRemaining = TimeSpan.FromMinutes(Math.Clamp(PomodoroWorkMinutes, 1, 120));
        PomodoroPhaseText = "Çalışma";
        UpdatePomodoroTimeText();
        IsPomodoroRunning = true;
        StopProfile();
        StopMusicMode();
        await ApplyPomodoroPhaseLightAsync();
        _pomodoroTimer.Start();
        StatusMessage = $"Pomodoro başladı: {Math.Round(PomodoroWorkMinutes)} dk odak.";
        await LogAsync("Information", "Pomodoro başlatıldı.", new { PomodoroWorkMinutes, PomodoroBreakMinutes });
    }

    public void StopPomodoro()
    {
        if (!IsPomodoroRunning)
        {
            _pomodoroTimer.Stop();
            return;
        }

        _pomodoroTimer.Stop();
        IsPomodoroRunning = false;
        PomodoroPhaseText = "Durduruldu";
        _pomodoroRemaining = TimeSpan.Zero;
        UpdatePomodoroTimeText();
        StatusMessage = "Pomodoro durduruldu.";
        _ = LogAsync("Information", "Pomodoro durduruldu.");
    }

    private async void OnPomodoroTick(object? sender, EventArgs e)
    {
        if (!IsPomodoroRunning)
        {
            return;
        }

        _pomodoroRemaining -= TimeSpan.FromSeconds(1);
        if (_pomodoroRemaining > TimeSpan.Zero)
        {
            UpdatePomodoroTimeText();
            return;
        }

        if (_pomodoroIsWorkPhase)
        {
            PomodoroCompletedSessions++;
            OnPropertyChanged(nameof(PomodoroCycleText));
        }

        _pomodoroIsWorkPhase = !_pomodoroIsWorkPhase;
        PomodoroPhaseText = _pomodoroIsWorkPhase ? "Çalışma" : "Mola";
        _pomodoroRemaining = TimeSpan.FromMinutes(Math.Clamp(
            _pomodoroIsWorkPhase ? PomodoroWorkMinutes : PomodoroBreakMinutes, 1, 120));
        UpdatePomodoroTimeText();
        await ApplyPomodoroPhaseLightAsync();
        await LogAsync("Information", "Pomodoro aşaması değişti.", new { PomodoroPhaseText, PomodoroCompletedSessions });
    }

    private async Task ApplyPomodoroPhaseLightAsync()
    {
        var connection = GetSelectedConnection();
        if (connection is null)
        {
            StatusMessage = "Pomodoro ışığı için ampul bağlı olmalı.";
            return;
        }

        try
        {
            if (_pomodoroIsWorkPhase)
            {
                await connection.SetPowerAsync(true);
                await connection.SetColorTemperatureAsync(PomodoroFocusMired);
                await connection.SetBrightnessAsync(254);
                _applyingRemoteState = true;
                try
                {
                    IsLightOn = true;
                    IsColorMode = false;
                    ColorTemperatureMired = PomodoroFocusMired;
                    BrightnessPercent = 100;
                }
                finally
                {
                    _applyingRemoteState = false;
                }
            }
            else
            {
                var (x, y) = HueColorConverter.ToXy(PomodoroBreakColor.R, PomodoroBreakColor.G, PomodoroBreakColor.B);
                await connection.SetPowerAsync(true);
                await connection.SetColorAndBrightnessAsync(x, y, 180);
                _applyingRemoteState = true;
                try
                {
                    IsLightOn = true;
                    IsColorMode = true;
                    _colorX = x;
                    _colorY = y;
                    BrightnessPercent = (180 - 1) / 253.0 * 100.0;
                }
                finally
                {
                    _applyingRemoteState = false;
                }
            }

            UpdateBulbColors();
            StatusMessage = _pomodoroIsWorkPhase
                ? "Odak aşaması: ampul beyaz."
                : "Mola aşaması: ampul yeşil.";
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Pomodoro ışığı uygulanamadı.", ErrorData(exception));
        }
    }

    private void UpdatePomodoroTimeText() => PomodoroTimeText = $"{(int)_pomodoroRemaining.TotalMinutes:00}:{_pomodoroRemaining.Seconds:00}";

    partial void OnIsPomodoroRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(PomodoroSummary));
        TogglePomodoroCommand.NotifyCanExecuteChanged();
    }

    partial void OnPomodoroPhaseTextChanged(string value) => OnPropertyChanged(nameof(PomodoroSummary));
    partial void OnPomodoroTimeTextChanged(string value) => OnPropertyChanged(nameof(PomodoroSummary));
    partial void OnPomodoroCompletedSessionsChanged(int value) => OnPropertyChanged(nameof(PomodoroCycleText));
}
