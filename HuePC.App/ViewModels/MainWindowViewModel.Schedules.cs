using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IScheduleStore _scheduleStore;
    private readonly DispatcherTimer _scheduleTimer;
    private readonly Dictionary<Guid, DateTimeOffset> _scheduleLastRuns = [];
    private readonly Dictionary<Guid, DateTimeOffset> _scheduleRampLastRuns = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _activeRamps = [];

    [ObservableProperty] private ScheduleViewModel? _selectedSchedule;

    public ObservableCollection<ScheduleViewModel> Schedules { get; } = [];
    public bool HasSchedules => Schedules.Count > 0;
    public bool IsSchedulesPage => CurrentPage == "Schedules";
    public bool IsScheduleSelected => SelectedSchedule is not null;

    private async Task LoadSchedulesAsync()
    {
        try
        {
            var stored = await _scheduleStore.LoadAsync();
            Schedules.Clear();
            foreach (var schedule in stored)
            {
                Schedules.Add(new ScheduleViewModel(schedule));
            }

            SelectedSchedule = Schedules.FirstOrDefault();
            OnPropertyChanged(nameof(HasSchedules));
            await LogAsync("Information", "Alarmlar yüklendi.", new { Count = Schedules.Count });
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Alarmlar yüklenemedi.", ErrorData(exception));
        }
    }

    private async Task SaveSchedulesAsync()
    {
        try
        {
            var models = new List<LightSchedule>();
            foreach (var schedule in Schedules)
            {
                if (schedule.TryCreateModel(out var model, out _))
                {
                    models.Add(model);
                }
            }

            await _scheduleStore.SaveAsync(models);
            OnPropertyChanged(nameof(HasSchedules));
        }
        catch (Exception exception)
        {
            StatusMessage = "Alarmlar kaydedilemedi.";
            await LogAsync("Error", "Alarmlar kaydedilemedi.", ErrorData(exception));
        }
    }

    [RelayCommand]
    private async Task AddScheduleAsync()
    {
        var schedule = new ScheduleViewModel(LightSchedule.CreateDefault(DateTimeOffset.Now));
        Schedules.Add(schedule);
        SelectedSchedule = schedule;
        OnPropertyChanged(nameof(HasSchedules));
        await SaveSchedulesAsync();
        StatusMessage = "Yeni alarm eklendi. Saati ve günleri düzenleyip kaydedin.";
    }

    [RelayCommand]
    private async Task DeleteScheduleAsync(ScheduleViewModel? schedule)
    {
        var target = schedule ?? SelectedSchedule;
        if (target is null) return;

        Schedules.Remove(target);
        _scheduleLastRuns.Remove(target.Id);
        _scheduleRampLastRuns.Remove(target.Id);
        CancelWakeUpRamp(target.Id);
        if (ReferenceEquals(SelectedSchedule, target))
        {
            SelectedSchedule = Schedules.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasSchedules));
        await SaveSchedulesAsync();
        StatusMessage = "Alarm silindi.";
        await LogAsync("Information", "Kullanıcı alarmı sildi.", new { target.Name });
    }

    [RelayCommand]
    private async Task SaveScheduleAsync()
    {
        if (SelectedSchedule is null) return;
        if (!SelectedSchedule.TryCreateModel(out _, out var error))
        {
            StatusMessage = error!;
            return;
        }

        await SaveSchedulesAsync();
        StatusMessage = "Alarm kaydedildi.";
        await LogAsync("Information", "Kullanıcı alarmı kaydetti.", new
        {
            SelectedSchedule.Name,
            SelectedSchedule.TimeText,
            Days = SelectedSchedule.DaysText,
            SelectedSchedule.ActionText,
            SelectedSchedule.Enabled
        });
    }

    [RelayCommand]
    private void CaptureCurrentState()
    {
        if (SelectedSchedule is null) return;
        if (!_hasLightState)
        {
            StatusMessage = "Önce ampule bağlanıp durumunun okunmasını bekleyin.";
            return;
        }

        var brightness = (byte)Math.Clamp(1 + Math.Round(BrightnessPercent / 100.0 * 253), 1, 254);
        var temperature = (ushort)Math.Clamp(Math.Round(ColorTemperatureMired), HueLightControlProtocol.ColorTemperatureMinimumMired, HueLightControlProtocol.ColorTemperatureMaximumMired);
        SelectedSchedule.CaptureState(brightness, IsColorMode, temperature, _colorX, _colorY);
        StatusMessage = "Ampulün mevcut durumu alarma kopyalandı.";
    }

    private async Task RunDueSchedulesAsync()
    {
        UpdateScheduleInsights();
        if (_disposed || Schedules.Count == 0) return;
        var now = DateTimeOffset.Now;
        foreach (var schedule in Schedules.ToArray())
        {
            schedule.TryCreateModel(out var model, out _);
            if (model is null) continue;

            _scheduleRampLastRuns.TryGetValue(model.Id, out var lastRamp);
            if (ScheduleEvaluator.IsRampDue(model, now, lastRamp == default ? null : lastRamp) && !_activeRamps.ContainsKey(model.Id))
            {
                _scheduleRampLastRuns[model.Id] = now;
                StartWakeUpRamp(model);
            }

            _scheduleLastRuns.TryGetValue(model.Id, out var lastRun);
            if (!ScheduleEvaluator.IsDue(model, now, lastRun == default ? null : lastRun)) continue;

            _scheduleLastRuns[model.Id] = now;
            await ApplyScheduleAsync(model);
        }
    }

    private void StartWakeUpRamp(LightSchedule schedule)
    {
        var cancellation = new CancellationTokenSource();
        _activeRamps[schedule.Id] = cancellation;
        _ = RunWakeUpRampAsync(schedule, cancellation.Token);
    }

    private void CancelWakeUpRamp(Guid scheduleId)
    {
        if (_activeRamps.Remove(scheduleId, out var cancellation))
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            cancellation.Dispose();
        }
    }

    private void CancelWakeUpRamps()
    {
        foreach (var scheduleId in _activeRamps.Keys.ToArray())
        {
            CancelWakeUpRamp(scheduleId);
        }
    }

    private async Task RunWakeUpRampAsync(LightSchedule schedule, CancellationToken cancellationToken)
    {
        try
        {
            StatusMessage = $"“{schedule.Name}” gün doğumu başladı: {schedule.WakeUpRampMinutes} dakikada yumuşak uyanış.";
            await LogAsync("Information", "Gün doğumu rampası başladı.", new { schedule.Name, schedule.WakeUpRampMinutes, schedule.Brightness, schedule.UseColor });
            var connection = await EnsureConnectedAsync();
            if (connection is null)
            {
                StatusMessage = $"“{schedule.Name}” gün doğumu uygulanamadı: ampule bağlanılamadı.";
                return;
            }

            StopProfile();
            StopMusicMode();
            var minutes = Math.Clamp(schedule.WakeUpRampMinutes, 1, 60);
            var targetMired = schedule.UseColor ? (ushort)350 : schedule.ColorTemperatureMired;
            var startMired = (ushort)455;
            await connection.SetPowerAsync(true);
            for (var elapsed = 0; elapsed <= minutes; elapsed++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var progress = elapsed / (double)minutes;
                var brightness = (byte)Math.Clamp(1 + schedule.Brightness * progress, 1, 254);
                var mired = (ushort)Math.Round(startMired + (targetMired - startMired) * progress);
                await connection.SetColorTemperatureAsync(mired);
                await connection.SetBrightnessAsync(brightness);
                StatusMessage = $"“{schedule.Name}” gün doğumu: %{Math.Round(brightness / 254.0 * 100)}";
                if (elapsed < minutes)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                }
            }

            StatusMessage = $"“{schedule.Name}” gün doğumu tamamlandı.";
            await LogAsync("Information", "Gün doğumu rampası tamamlandı.", new { schedule.Name });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await LogAsync("Error", "Gün doğumu rampası sırasında hata oluştu.", ErrorData(exception));
        }
        finally
        {
            if (_activeRamps.TryGetValue(schedule.Id, out var cancellation) && cancellation.Token == cancellationToken)
            {
                _activeRamps.Remove(schedule.Id);
            }
        }
    }

    private async Task ApplyScheduleAsync(LightSchedule schedule)
    {
        try
        {
            StopProfile();
            StopMusicMode();
            var connection = await EnsureConnectedAsync();
            if (connection is null)
            {
                StatusMessage = $"“{schedule.Name}” alarmı uygulanamadı: ampule bağlanılamadı.";
                await LogAsync("Warning", "Alarm uygulanamadı; bağlantı yok.", new { schedule.Name });
                return;
            }

            if (schedule.Action == ScheduleAction.TurnOff)
            {
                var offResult = await connection.SetPowerAsync(false);
                StatusMessage = offResult.IsSuccess ? $"“{schedule.Name}” alarmı: ampul kapatıldı." : $"“{schedule.Name}” alarmı uygulanamadı.";
                await LogAsync(offResult.IsSuccess ? "Information" : "Warning", "Alarm uygulandı (kapat).", new { schedule.Name, offResult.Status, offResult.Error });
                return;
            }

            await connection.SetPowerAsync(true);
            if (schedule.UseColor)
            {
                await connection.SetColorAsync(schedule.ColorX, schedule.ColorY);
            }
            else
            {
                await connection.SetColorTemperatureAsync(schedule.ColorTemperatureMired);
            }

            var brightnessResult = await connection.SetBrightnessAsync(schedule.Brightness);
            StatusMessage = brightnessResult.IsSuccess ? $"“{schedule.Name}” alarmı uygulandı." : $"“{schedule.Name}” alarmı kısmen uygulandı.";
            await LogAsync("Information", "Alarm uygulandı (aç).", new
            {
                schedule.Name,
                schedule.Brightness,
                schedule.UseColor,
                schedule.ColorTemperatureMired,
                schedule.ColorX,
                schedule.ColorY,
                brightnessResult.Status
            });
        }
        catch (Exception exception)
        {
            await LogAsync("Error", "Alarm uygulanırken hata oluştu.", ErrorData(exception));
        }
    }

    private async Task<IBleConnection?> EnsureConnectedAsync()
    {
        var selected = SelectedDevice;
        if (selected is null) return null;
        var key = selected.Info.DeviceKey;
        if (_connections.TryGetValue(key, out var existing))
        {
            if (existing.ConnectionState == "Connected") return existing;
            DetachConnection(existing);
            _connections.Remove(key);
            await existing.DisposeAsync();
        }

        try
        {
            var connection = await _transport.ConnectAsync(selected.Info);
            _connections[key] = connection;
            AttachConnection(connection);
            await _deviceMemoryStore.SaveAsync(selected.Info);
            _rememberedDevice = new RememberedBleDevice(selected.Info, DateTimeOffset.UtcNow);
            SelectedConnectionState = connection.ConnectionState == "Connected" ? "Bağlı" : "Bağlanıyor";
            RaiseConnectionProperties();
            await LogAsync("Information", "Zamanlayıcı ampule bağlandı.", new { selected.Info.Address });
            return connection;
        }
        catch (Exception exception)
        {
            SelectedConnectionState = "Bağlantı kurulamadı";
            RaiseConnectionProperties();
            await LogAsync("Error", "Zamanlayıcı ampule bağlanamadı.", ErrorData(exception));
            return null;
        }
    }

    partial void OnSelectedScheduleChanged(ScheduleViewModel? value) => OnPropertyChanged(nameof(IsScheduleSelected));

    /// <summary>Next run countdown and a warning when two enabled alarms fire within five minutes.</summary>
    public string NextScheduleText { get; private set; } = "Etkin alarm yok";

    public string ScheduleConflictText { get; private set; } = string.Empty;

    public bool HasScheduleConflict => ScheduleConflictText.Length > 0;

    private void UpdateScheduleInsights()
    {
        if (_disposed) return;
        var now = DateTimeOffset.Now;

        DateTimeOffset? next = null;
        ScheduleViewModel? nextSchedule = null;
        foreach (var schedule in Schedules)
        {
            if (!schedule.Enabled || schedule.ParsedTime is not { } time) continue;
            for (var offset = 0; offset < 8; offset++)
            {
                var day = now.Date.AddDays(offset);
                if (!DayMatches(schedule.CurrentDays, day.DayOfWeek)) continue;
                var candidate = new DateTimeOffset(day + time.ToTimeSpan(), now.Offset);
                if (candidate <= now) continue;
                if (next is null || candidate < next)
                {
                    next = candidate;
                    nextSchedule = schedule;
                }

                break;
            }
        }

        var nextText = next is null
            ? "Etkin alarm yok"
            : $"Sonraki: {nextSchedule!.Name} · {next.Value:d MMM HH:mm} · {DescribeRemaining(next.Value - now)}";
        if (nextText != NextScheduleText)
        {
            NextScheduleText = nextText;
            OnPropertyChanged(nameof(NextScheduleText));
        }

        var conflictText = string.Empty;
        var enabled = Schedules.Where(schedule => schedule.Enabled && schedule.ParsedTime is not null).ToArray();
        for (var first = 0; first < enabled.Length && conflictText.Length == 0; first++)
        {
            for (var second = first + 1; second < enabled.Length; second++)
            {
                if ((enabled[first].CurrentDays & enabled[second].CurrentDays) == ScheduleDays.None) continue;
                var firstTime = enabled[first].ParsedTime!.Value;
                var secondTime = enabled[second].ParsedTime!.Value;
                var minutes = Math.Abs(firstTime.ToTimeSpan().TotalMinutes - secondTime.ToTimeSpan().TotalMinutes);
                if (minutes <= 5)
                {
                    conflictText = $"Uyarı: “{enabled[first].Name}” ve “{enabled[second].Name}” {(int)minutes} dakika arayla çalışıyor.";
                    break;
                }
            }
        }

        if (conflictText != ScheduleConflictText)
        {
            ScheduleConflictText = conflictText;
            OnPropertyChanged(nameof(ScheduleConflictText));
            OnPropertyChanged(nameof(HasScheduleConflict));
        }
    }

    private static string DescribeRemaining(TimeSpan remaining) => remaining.TotalHours >= 1
        ? $"{(int)remaining.TotalHours} sa {remaining.Minutes} dk sonra"
        : $"{Math.Max(0, remaining.Minutes)} dk sonra";

    private static bool DayMatches(ScheduleDays days, DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => days.HasFlag(ScheduleDays.Monday),
        DayOfWeek.Tuesday => days.HasFlag(ScheduleDays.Tuesday),
        DayOfWeek.Wednesday => days.HasFlag(ScheduleDays.Wednesday),
        DayOfWeek.Thursday => days.HasFlag(ScheduleDays.Thursday),
        DayOfWeek.Friday => days.HasFlag(ScheduleDays.Friday),
        DayOfWeek.Saturday => days.HasFlag(ScheduleDays.Saturday),
        _ => days.HasFlag(ScheduleDays.Sunday)
    };

    /// <summary>Applies a drag on the weekly timeline: time always, day only when it changed.</summary>
    public async Task MoveScheduleFromTimelineAsync(Guid scheduleId, TimeOnly time, int dayIndex)
    {
        var schedule = Schedules.FirstOrDefault(item => item.Id == scheduleId);
        if (schedule is null) return;

        schedule.TimeText = time.ToString("HH\\:mm");
        var day = Controls.WeekTimeline.DayAt(dayIndex);
        var changedDay = schedule.CurrentDays != day;
        if (changedDay)
        {
            schedule.SetSingleDay(day);
        }

        await SaveSchedulesAsync();
        UpdateScheduleInsights();
        StatusMessage = changedDay
            ? $"“{schedule.Name}” {schedule.DaysText} {time:HH\\:mm} olarak taşındı."
            : $"“{schedule.Name}” {time:HH\\:mm} olarak taşındı.";
        await LogAsync("Information", "Alarm zaman çizelgesinden taşındı.", new { schedule.Name, Time = time.ToString(), Day = day.ToString() });
    }
}
