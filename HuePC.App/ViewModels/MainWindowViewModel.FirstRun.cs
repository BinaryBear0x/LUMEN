using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HuePC.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _hasCompletedFirstRun;

    [ObservableProperty] private bool _isFirstRunVisible;
    [ObservableProperty] private int _firstRunStep = 1;
    [ObservableProperty] private bool _firstRunNotificationChoice = true;
    [ObservableProperty] private bool _firstRunBusyLightChoice = true;
    [ObservableProperty] private bool _firstRunCircadianChoice;
    [ObservableProperty] private bool _firstRunEarthquakeChoice;

    public int FirstRunStepCount => 4;

    public string FirstRunStepTitle => FirstRunStep switch
    {
        2 => "Ampulünüzü bağlayın",
        3 => "Bildirim izni",
        4 => "Hızlı kurulum",
        _ => "LUMEN'e hoş geldiniz"
    };

    public string FirstRunStepHint => FirstRunStep switch
    {
        2 => "Ampulü açın ve yakına getirin; LUMEN yakındaki Hue reklamlarını listeler. Bağlanmak için ampulü seçip Bağlan'a basın.",
        3 => "Bildirim gelince ampulün yanıp sönmesi için Windows bildirim erişimi gerekir; izni bu adımda verebilirsiniz.",
        4 => "İstediğiniz özellikleri seçin; istediğiniz zaman sayfalarından açıp kapatabilirsiniz.",
        _ => "LUMEN, Philips Hue ampulünüzü Bluetooth üzerinden bağlar; alarmlar, bildirimler ve ortam ışığı tek uygulamada."
    };

    public bool IsFirstRunStep1 => FirstRunStep == 1;
    public bool IsFirstRunStep2 => FirstRunStep == 2;
    public bool IsFirstRunStep3 => FirstRunStep == 3;
    public bool IsFirstRunStep4 => FirstRunStep == 4;
    public bool CanGoBackFirstRun => FirstRunStep > 1;

    [RelayCommand]
    private void NextFirstRunStep()
    {
        if (FirstRunStep >= FirstRunStepCount) return;
        FirstRunStep++;
    }

    [RelayCommand]
    private void PreviousFirstRunStep()
    {
        if (FirstRunStep <= 1) return;
        FirstRunStep--;
    }

    [RelayCommand]
    private void AllowFirstRunNotifications() => _ = ApplyNotificationBlinkAsync(true);

    [RelayCommand]
    private async Task FinishFirstRunAsync()
    {
        if (FirstRunNotificationChoice && !NotificationBlinkEnabled)
        {
            NotificationBlinkEnabled = true;
            await ApplyNotificationBlinkAsync(true);
        }

        if (FirstRunBusyLightChoice && !BusyLightEnabled)
        {
            BusyLightEnabled = true;
        }

        if (FirstRunCircadianChoice && !CircadianEnabled)
        {
            CircadianEnabled = true;
        }

        if (FirstRunEarthquakeChoice && !EarthquakeAlertEnabled)
        {
            EarthquakeAlertEnabled = true;
        }

        _hasCompletedFirstRun = true;
        IsFirstRunVisible = false;
        await SaveAllSettingsAsync();
        StatusMessage = "Kurulum tamamlandı. İyi kullanımlar!";
        await LogAsync("Information", "İlk açılış kurulumu tamamlandı.", new
        {
            FirstRunNotificationChoice,
            FirstRunBusyLightChoice,
            FirstRunCircadianChoice,
            FirstRunEarthquakeChoice
        });
    }

    [RelayCommand]
    private async Task SkipFirstRunAsync()
    {
        _hasCompletedFirstRun = true;
        IsFirstRunVisible = false;
        await SaveAllSettingsAsync();
        await LogAsync("Information", "İlk açılış kurulumu atlandı.");
    }

    partial void OnFirstRunStepChanged(int value)
    {
        OnPropertyChanged(nameof(FirstRunStepTitle));
        OnPropertyChanged(nameof(FirstRunStepHint));
        OnPropertyChanged(nameof(IsFirstRunStep1));
        OnPropertyChanged(nameof(IsFirstRunStep2));
        OnPropertyChanged(nameof(IsFirstRunStep3));
        OnPropertyChanged(nameof(IsFirstRunStep4));
        OnPropertyChanged(nameof(CanGoBackFirstRun));
    }
}
