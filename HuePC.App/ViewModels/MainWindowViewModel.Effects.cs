using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.App.ViewModels;

public sealed partial class EffectOptionViewModel : ObservableObject
{
    public EffectOptionViewModel(HueEffect effect, string name)
    {
        Effect = effect;
        Name = name;
    }

    public HueEffect Effect { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isFavorite;
}

public sealed partial class MainWindowViewModel
{
    private readonly DispatcherTimer _effectSpeedTimer;

    [ObservableProperty] private EffectOptionViewModel? _activeEffect;
    [ObservableProperty] private double _effectSpeed = 50;

    public ObservableCollection<EffectOptionViewModel> Effects { get; } =
    [
        new(HueEffect.Candle, "Mum"),
        new(HueEffect.Fireplace, "Şömine"),
        new(HueEffect.Prism, "Prizma"),
        new(HueEffect.Sparkle, "Parıltı"),
        new(HueEffect.Opal, "Opal"),
        new(HueEffect.Glisten, "Kıvılcım"),
        new(HueEffect.Underwater, "Deniz altı"),
        new(HueEffect.Cosmos, "Kozmos"),
        new(HueEffect.Sunbeam, "Güneş ışığı"),
        new(HueEffect.Enchant, "Büyü")
    ];

    [RelayCommand]
    private void ToggleEffectFavorite(EffectOptionViewModel? option)
    {
        if (option is null) return;
        option.IsFavorite = !option.IsFavorite;
        ReorderFavoriteEffects();
        StatusMessage = option.IsFavorite ? $"“{option.Name}” favorilere eklendi." : $"“{option.Name}” favorilerden çıkarıldı.";
        _ = SaveAllSettingsAsync();
    }

    public IReadOnlyList<string> FavoriteEffectNames => Effects.Where(item => item.IsFavorite).Select(item => item.Name).ToArray();

    private void ApplyFavoriteEffects(IReadOnlyList<string>? names)
    {
        if (names is null) return;
        foreach (var effect in Effects)
        {
            effect.IsFavorite = names.Contains(effect.Name, StringComparer.OrdinalIgnoreCase);
        }

        ReorderFavoriteEffects();
    }

    private void ReorderFavoriteEffects()
    {
        var ordered = Effects.OrderByDescending(item => item.IsFavorite).ToArray();
        for (var target = 0; target < ordered.Length; target++)
        {
            var currentIndex = Effects.IndexOf(ordered[target]);
            if (currentIndex != target)
            {
                Effects.Move(currentIndex, target);
            }
        }
    }

    public string ActiveEffectName => ActiveEffect?.Name ?? "Yok";
    public string EffectSpeedLabel => $"{Math.Round(EffectSpeed):0}%";
    public HueEffect PreviewEffect => ActiveEffect?.Effect ?? Effects[0].Effect;

    private static byte ToDeviceSpeed(double percent) =>
        (byte)Math.Clamp(1 + Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * 253), 1, 254);

    private static double FromDeviceSpeed(byte speed) =>
        Math.Clamp((speed - 1) / 253.0 * 100.0, 0, 100);

    [RelayCommand(CanExecute = nameof(CanControlLight))]
    private async Task ToggleEffectAsync(EffectOptionViewModel? option)
    {
        if (option is null) return;
        var connection = GetSelectedConnection();
        if (connection is null) return;

        StopProfile();
        StopMusicMode();
        var speed = ToDeviceSpeed(EffectSpeed);
        var stopping = ActiveEffect == option;
        var effect = stopping ? HueEffect.None : option.Effect;
        var result = await connection.SetEffectAsync(effect, speed);
        if (result.IsSuccess)
        {
            SetActiveEffect(stopping ? null : option);
            StatusMessage = stopping ? "Efekt durduruldu." : $"Efekt: {option.Name}.";
        }
        else
        {
            StatusMessage = $"Efekt uygulanamadı: {result.Error ?? result.Status}";
        }

        await LogAsync(result.IsSuccess ? "Information" : "Warning", "Ampul efekt komutu işlendi.", new
        {
            Effect = effect.ToString(),
            Speed = speed,
            result.Status,
            result.Error
        });
    }

    private void SetActiveEffect(EffectOptionViewModel? option)
    {
        foreach (var item in Effects)
        {
            item.IsActive = ReferenceEquals(item, option);
        }

        ActiveEffect = option;
        OnPropertyChanged(nameof(ActiveEffectName));
        OnPropertyChanged(nameof(PreviewEffect));
    }

    partial void OnEffectSpeedChanged(double value)
    {
        OnPropertyChanged(nameof(EffectSpeedLabel));
        if (_applyingRemoteState || ActiveEffect is null || !IsSelectedDeviceConnected) return;
        _effectSpeedTimer.Stop();
        _effectSpeedTimer.Start();
    }

    private async Task SendEffectSpeedAsync()
    {
        if (ActiveEffect is null) return;
        var connection = GetSelectedConnection();
        if (connection is null) return;
        var speed = ToDeviceSpeed(EffectSpeed);
        var result = await connection.SetEffectSpeedAsync(speed);
        if (!result.IsSuccess)
        {
            StatusMessage = $"Efekt hızı uygulanamadı: {result.Error ?? result.Status}";
            await LogAsync("Warning", "Ampul efekt hızı uygulanamadı.", new { speed, result.Status, result.Error });
        }
    }

    private void ApplyEffectState(HueLightState state)
    {
        if (state.Effect is not null)
        {
            var option = state.Effect == 0 ? null : Effects.FirstOrDefault(item => (byte)item.Effect == state.Effect);
            SetActiveEffect(option);
        }

        if (state.EffectSpeed is not null)
        {
            EffectSpeed = FromDeviceSpeed(state.EffectSpeed.Value);
            OnPropertyChanged(nameof(EffectSpeedLabel));
        }
    }
}
