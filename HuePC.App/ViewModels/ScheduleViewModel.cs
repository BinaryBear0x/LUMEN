using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.App.ViewModels;

public sealed partial class ScheduleViewModel : ObservableObject
{
    private byte _brightness;
    private bool _useColor;
    private ushort _colorTemperatureMired;
    private ushort _colorX;
    private ushort _colorY;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _timeText;
    [ObservableProperty] private bool _monday;
    [ObservableProperty] private bool _tuesday;
    [ObservableProperty] private bool _wednesday;
    [ObservableProperty] private bool _thursday;
    [ObservableProperty] private bool _friday;
    [ObservableProperty] private bool _saturday;
    [ObservableProperty] private bool _sunday;
    [ObservableProperty] private bool _turnOn;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _stateSummary = "—";
    [ObservableProperty] private int _wakeUpRampMinutes;

    public ScheduleViewModel(LightSchedule model)
    {
        Id = model.Id;
        _name = model.Name;
        _timeText = model.Time.ToString("HH\\:mm", CultureInfo.InvariantCulture);
        _monday = model.Days.HasFlag(ScheduleDays.Monday);
        _tuesday = model.Days.HasFlag(ScheduleDays.Tuesday);
        _wednesday = model.Days.HasFlag(ScheduleDays.Wednesday);
        _thursday = model.Days.HasFlag(ScheduleDays.Thursday);
        _friday = model.Days.HasFlag(ScheduleDays.Friday);
        _saturday = model.Days.HasFlag(ScheduleDays.Saturday);
        _sunday = model.Days.HasFlag(ScheduleDays.Sunday);
        _turnOn = model.Action == ScheduleAction.TurnOnWithState;
        _enabled = model.Enabled;
        _brightness = model.Brightness;
        _useColor = model.UseColor;
        _colorTemperatureMired = model.ColorTemperatureMired;
        _colorX = model.ColorX;
        _colorY = model.ColorY;
        _wakeUpRampMinutes = model.Action == ScheduleAction.TurnOnWithState ? Math.Clamp(model.WakeUpRampMinutes, 0, 60) : 0;
        RefreshSummary();
    }

    public Guid Id { get; }
    public string DaysText => ScheduleEvaluator.DescribeDays(CurrentDays);
    public string ActionText => TurnOn ? "Aç" : "Kapat";
    public TimeOnly? ParsedTime =>
        TimeOnly.TryParse(TimeText, CultureInfo.InvariantCulture, out var time) ||
        TimeOnly.TryParse(TimeText, CultureInfo.CurrentCulture, out time)
            ? time
            : null;

    public bool TurnOff
    {
        get => !TurnOn;
        set
        {
            if (value && TurnOn)
            {
                TurnOn = false;
            }
        }
    }

    public ScheduleDays CurrentDays =>
        (Monday ? ScheduleDays.Monday : ScheduleDays.None) |
        (Tuesday ? ScheduleDays.Tuesday : ScheduleDays.None) |
        (Wednesday ? ScheduleDays.Wednesday : ScheduleDays.None) |
        (Thursday ? ScheduleDays.Thursday : ScheduleDays.None) |
        (Friday ? ScheduleDays.Friday : ScheduleDays.None) |
        (Saturday ? ScheduleDays.Saturday : ScheduleDays.None) |
        (Sunday ? ScheduleDays.Sunday : ScheduleDays.None);

    public void CaptureState(byte brightness, bool useColor, ushort colorTemperatureMired, ushort colorX, ushort colorY)
    {
        _brightness = brightness;
        _useColor = useColor;
        _colorTemperatureMired = colorTemperatureMired;
        _colorX = colorX;
        _colorY = colorY;
        RefreshSummary();
    }

    public void SetSingleDay(ScheduleDays day)
    {
        Monday = day.HasFlag(ScheduleDays.Monday);
        Tuesday = day.HasFlag(ScheduleDays.Tuesday);
        Wednesday = day.HasFlag(ScheduleDays.Wednesday);
        Thursday = day.HasFlag(ScheduleDays.Thursday);
        Friday = day.HasFlag(ScheduleDays.Friday);
        Saturday = day.HasFlag(ScheduleDays.Saturday);
        Sunday = day.HasFlag(ScheduleDays.Sunday);
    }

    public bool TryCreateModel(out LightSchedule model, out string? error)
    {
        model = default!;
        if (!TimeOnly.TryParse(TimeText, CultureInfo.InvariantCulture, out var time) &&
            !TimeOnly.TryParse(TimeText, CultureInfo.CurrentCulture, out time))
        {
            error = "Saat SS:DD biçiminde olmalı, örn. 20:30.";
            return false;
        }

        if (CurrentDays == ScheduleDays.None)
        {
            error = "En az bir gün seçin.";
            return false;
        }

        model = new LightSchedule(
            Id,
            string.IsNullOrWhiteSpace(Name) ? "Alarm" : Name.Trim(),
            time,
            CurrentDays,
            TurnOn ? ScheduleAction.TurnOnWithState : ScheduleAction.TurnOff,
            _brightness,
            _useColor,
            _colorTemperatureMired,
            _colorX,
            _colorY,
            Enabled,
            TurnOn ? Math.Clamp(WakeUpRampMinutes, 0, 60) : 0);
        error = null;
        return true;
    }

    private void RefreshSummary()
    {
        if (!TurnOn)
        {
            StateSummary = "Ampul kapatılır";
            return;
        }

        var percent = (int)Math.Round((_brightness - 1) / 253.0 * 100);
        var ramp = WakeUpRampMinutes > 0 ? $" · gün doğumu {WakeUpRampMinutes} dk" : string.Empty;
        StateSummary = _useColor
            ? $"Renkli · %{percent} parlaklık{ramp}"
            : $"Beyaz · {Math.Round(1000000.0 / Math.Max(1, (int)_colorTemperatureMired)):0} K · %{percent} parlaklık{ramp}";
    }

    partial void OnTurnOnChanged(bool value)
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(TurnOff));
        RefreshSummary();
    }

    partial void OnWakeUpRampMinutesChanged(int value)
    {
        if (value < 0) WakeUpRampMinutes = 0;
        OnPropertyChanged(nameof(WakeUpRampText));
        RefreshSummary();
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnTimeTextChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(EnabledText));

    partial void OnMondayChanged(bool value) => RaiseDaysChanged();
    partial void OnTuesdayChanged(bool value) => RaiseDaysChanged();
    partial void OnWednesdayChanged(bool value) => RaiseDaysChanged();
    partial void OnThursdayChanged(bool value) => RaiseDaysChanged();
    partial void OnFridayChanged(bool value) => RaiseDaysChanged();
    partial void OnSaturdayChanged(bool value) => RaiseDaysChanged();
    partial void OnSundayChanged(bool value) => RaiseDaysChanged();

    private void RaiseDaysChanged()
    {
        OnPropertyChanged(nameof(DaysText));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    public string DisplayTitle => $"{TimeText} · {(string.IsNullOrWhiteSpace(Name) ? "Alarm" : Name)}";
    public string EnabledText => Enabled ? "Etkin" : "Kapalı";
    public string WakeUpRampText => WakeUpRampMinutes > 0 ? $"{WakeUpRampMinutes} dk" : "Kapalı";
}
