using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.App.ViewModels;

public sealed partial class LightProfileViewModel : ObservableObject
{
    public LightProfileViewModel(string name, params Color[] colors)
    {
        Name = name;
        Colors = colors;
        var brush = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
        for (var index = 0; index < colors.Length; index++)
        {
            brush.GradientStops.Add(new GradientStop(colors[index], index / (double)Math.Max(1, colors.Length - 1)));
        }

        brush.Freeze();
        PreviewBrush = brush;
    }

    public string Name { get; }
    public IReadOnlyList<Color> Colors { get; }
    public Brush PreviewBrush { get; }
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFavorite;
    public bool IsCustom { get; init; }
}

public sealed partial class ProfileColorSlotViewModel : ObservableObject
{
    [ObservableProperty] private Color _color;

    public ProfileColorSlotViewModel(Color color) => _color = color;

    public string Hex => HexColor.Format(new HueRgb(Color.R, Color.G, Color.B));

    public Brush Brush => new SolidColorBrush(Color);

    partial void OnColorChanged(Color value)
    {
        OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(Brush));
    }
}

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _profileCts;
    private Guid _profileRunId;

    [ObservableProperty] private LightProfileViewModel? _activeProfile;
    [ObservableProperty] private double _profileCycleSeconds = 20;
    [ObservableProperty] private bool _isProfileRunning;

    public ObservableCollection<LightProfileViewModel> Profiles { get; } =
    [
        new("Gün batımı", Color.FromRgb(0xFF, 0x7A, 0x3D), Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xB4, 0x4B, 0xD6), Color.FromRgb(0xFF, 0x9F, 0x1C)),
        new("Okyanus", Color.FromRgb(0x00, 0xC2, 0xA8), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x2B, 0x3A, 0x8F), Color.FromRgb(0x00, 0xE0, 0xFF)),
        new("Orman", Color.FromRgb(0x2E, 0xCC, 0x71), Color.FromRgb(0xA3, 0xE6, 0x35), Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x4A, 0xDE, 0x80)),
        new("Kutup", Color.FromRgb(0x00, 0xFF, 0xC6), Color.FromRgb(0x7C, 0x4D, 0xFF), Color.FromRgb(0xFF, 0x4F, 0xD8), Color.FromRgb(0x00, 0xB3, 0xFF)),
        new("Ateş", Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0xFF, 0x6B, 0x2B))
    ];

    [ObservableProperty] private string _newProfileName = string.Empty;

    public ObservableCollection<ProfileColorSlotViewModel> ProfileBuilderColors { get; } =
    [
        new(Color.FromRgb(0xFF, 0x7A, 0x3D)),
        new(Color.FromRgb(0x0A, 0x84, 0xFF))
    ];

    public bool CanSaveCustomProfile =>
        !string.IsNullOrWhiteSpace(NewProfileName) &&
        ProfileBuilderColors.Count >= 2 &&
        !Profiles.Any(profile => profile.IsCustom && string.Equals(profile.Name, NewProfileName.Trim(), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> FavoriteProfileNames => Profiles.Where(profile => profile.IsFavorite).Select(profile => profile.Name).ToArray();

    public IReadOnlyList<CustomProfileSetting> CustomProfileSettings => Profiles
        .Where(profile => profile.IsCustom)
        .Select(profile => new CustomProfileSetting(profile.Name, profile.Colors.Select(color => HexColor.Format(new HueRgb(color.R, color.G, color.B))).ToArray()))
        .ToArray();

    [RelayCommand]
    private void ToggleProfileFavorite(LightProfileViewModel? profile)
    {
        if (profile is null) return;
        profile.IsFavorite = !profile.IsFavorite;
        ReorderFavoriteProfiles();
        StatusMessage = profile.IsFavorite ? $"“{profile.Name}” favorilere eklendi." : $"“{profile.Name}” favorilerden çıkarıldı.";
        _ = SaveAllSettingsAsync();
    }

    [RelayCommand]
    private void AddProfileColor()
    {
        if (ProfileBuilderColors.Count >= 8)
        {
            StatusMessage = "Bir profilde en fazla sekiz renk olabilir.";
            return;
        }

        ProfileBuilderColors.Add(new ProfileColorSlotViewModel(Color.FromRgb(0x7A, 0xD8, 0x5B)));
    }

    [RelayCommand]
    private void RemoveProfileColor(ProfileColorSlotViewModel? slot)
    {
        if (slot is null || ProfileBuilderColors.Count <= 2)
        {
            StatusMessage = "Bir profilde en az iki renk olmalı.";
            return;
        }

        ProfileBuilderColors.Remove(slot);
    }

    [RelayCommand]
    private void MoveProfileColorUp(ProfileColorSlotViewModel? slot)
    {
        if (slot is null) return;
        var index = ProfileBuilderColors.IndexOf(slot);
        if (index > 0)
        {
            ProfileBuilderColors.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private async Task SaveCustomProfileAsync()
    {
        if (!CanSaveCustomProfile)
        {
            StatusMessage = "Profil adı girin; en az iki renk olmalı ve ad benzersiz olmalı.";
            return;
        }

        var colors = ProfileBuilderColors.Select(slot => slot.Color).ToArray();
        Profiles.Add(new LightProfileViewModel(NewProfileName.Trim(), colors) { IsCustom = true });
        StatusMessage = $"“{NewProfileName.Trim()}” profili kaydedildi.";
        await LogAsync("Information", "Özel ışık profili kaydedildi.", new { Name = NewProfileName.Trim(), Colors = colors.Length });
        NewProfileName = string.Empty;
        OnPropertyChanged(nameof(CanSaveCustomProfile));
        await SaveAllSettingsAsync();
    }

    [RelayCommand]
    private async Task DeleteCustomProfileAsync(LightProfileViewModel? profile)
    {
        if (profile is null || !profile.IsCustom) return;
        Profiles.Remove(profile);
        if (ReferenceEquals(ActiveProfile, profile))
        {
            StopProfile();
            ActiveProfile = null;
        }

        StatusMessage = $"“{profile.Name}” profili silindi.";
        await SaveAllSettingsAsync();
    }

    private void ApplyCustomProfiles(IReadOnlyList<CustomProfileSetting>? customProfiles)
    {
        if (customProfiles is null) return;
        foreach (var custom in customProfiles)
        {
            var colors = custom.ColorHexes
                .Select(hex => HexColor.Parse(hex, new HueRgb(0xFF, 0xFF, 0xFF)))
                .Select(rgb => Color.FromRgb(rgb.R, rgb.G, rgb.B))
                .ToArray();
            if (colors.Length < 2 || Profiles.Any(profile => string.Equals(profile.Name, custom.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Profiles.Add(new LightProfileViewModel(custom.Name, colors) { IsCustom = true });
        }
    }

    private void ApplyFavoriteProfiles(IReadOnlyList<string>? names)
    {
        if (names is null) return;
        foreach (var profile in Profiles)
        {
            profile.IsFavorite = names.Contains(profile.Name, StringComparer.OrdinalIgnoreCase);
        }

        ReorderFavoriteProfiles();
    }

    private void ReorderFavoriteProfiles()
    {
        var ordered = Profiles.OrderByDescending(profile => profile.IsFavorite).ToArray();
        for (var target = 0; target < ordered.Length; target++)
        {
            var currentIndex = Profiles.IndexOf(ordered[target]);
            if (currentIndex != target)
            {
                Profiles.Move(currentIndex, target);
            }
        }
    }

    public string ProfileStatusText => IsProfileRunning ? $"{ActiveProfile?.Name} · çalışıyor" : "Durduruldu";
    public string ProfileCycleLabel => $"{Math.Round(ProfileCycleSeconds):0} sn";
    public LightProfileViewModel PreviewProfile => ActiveProfile ?? Profiles[0];

    partial void OnActiveProfileChanged(LightProfileViewModel? value) => OnPropertyChanged(nameof(PreviewProfile));

    [RelayCommand(CanExecute = nameof(CanControlLight))]
    private async Task ToggleProfileAsync(LightProfileViewModel? profile)
    {
        if (profile is null) return;
        if (IsProfileRunning && ReferenceEquals(ActiveProfile, profile))
        {
            StopProfile();
            StatusMessage = "Işık profili durduruldu.";
            return;
        }

        var connection = GetSelectedConnection();
        if (connection is null) return;

        StopProfile();
        StopMusicMode();
        await connection.SetEffectAsync(HueEffect.None, 128);
        SetActiveEffect(null);

        foreach (var item in Profiles)
        {
            item.IsRunning = ReferenceEquals(item, profile);
        }

        ActiveProfile = profile;
        IsProfileRunning = true;
        OnPropertyChanged(nameof(ProfileStatusText));

        var runId = Guid.NewGuid();
        _profileRunId = runId;
        _profileCts = new CancellationTokenSource();
        _ = RunProfileAsync(profile, connection, runId, _profileCts.Token);

        StatusMessage = $"Işık profili başlatıldı: {profile.Name}.";
        await LogAsync("Information", "Işık profili başlatıldı.", new { profile.Name, CycleSeconds = ProfileCycleSeconds });
    }

    private async Task RunProfileAsync(LightProfileViewModel profile, IBleConnection connection, Guid runId, CancellationToken cancellationToken)
    {
        var stepDelay = TimeSpan.FromMilliseconds(350);
        var startedAt = Stopwatch.StartNew();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var cycle = Math.Clamp(ProfileCycleSeconds, 6, 120);
                var progress = startedAt.Elapsed.TotalSeconds % cycle / cycle;
                var scaled = progress * profile.Colors.Count;
                var index = (int)scaled % profile.Colors.Count;
                var next = (index + 1) % profile.Colors.Count;
                var blend = scaled - Math.Floor(scaled);
                var color = Lerp(profile.Colors[index], profile.Colors[next], blend);
                var (x, y) = HueColorConverter.ToXy(color.R, color.G, color.B);

                var result = await connection.SetColorAsync(x, y, cancellationToken);
                if (!result.IsSuccess)
                {
                    StatusMessage = $"Profil komutu uygulanamadı: {result.Error ?? result.Status}";
                    await LogAsync("Warning", "Profil renk komutu uygulanamadı.", new { result.Status, result.Error });
                    break;
                }

                await Task.Delay(stepDelay, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await LogAsync("Error", "Profil çalışırken hata oluştu.", ErrorData(exception));
        }
        finally
        {
            if (_profileRunId == runId)
            {
                foreach (var item in Profiles)
                {
                    item.IsRunning = false;
                }

                IsProfileRunning = false;
                OnPropertyChanged(nameof(ProfileStatusText));
                await LogAsync("Information", "Işık profili durdu.", new { profile.Name });
            }
        }
    }

    public void StopProfile()
    {
        if (_profileCts is not null)
        {
            _profileCts.Cancel();
            _profileCts.Dispose();
            _profileCts = null;
        }

        if (IsProfileRunning)
        {
            IsProfileRunning = false;
            OnPropertyChanged(nameof(ProfileStatusText));
        }
    }

    partial void OnProfileCycleSecondsChanged(double value) => OnPropertyChanged(nameof(ProfileCycleLabel));

    partial void OnNewProfileNameChanged(string value) => OnPropertyChanged(nameof(CanSaveCustomProfile));

    private static Color Lerp(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
