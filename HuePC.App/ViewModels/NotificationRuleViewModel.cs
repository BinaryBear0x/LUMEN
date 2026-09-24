using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.App.ViewModels;

public sealed partial class NotificationRuleViewModel : ObservableObject
{
    [ObservableProperty] private string _appName;
    [ObservableProperty] private string _colorHex;
    [ObservableProperty] private bool _stayUntilRead;
    [ObservableProperty] private bool _enabled;

    public NotificationRuleViewModel(NotificationRule model)
        : this(model.AppName, model.ColorHex, model.StayUntilRead, model.Enabled)
    {
    }

    public NotificationRuleViewModel(string appName, string colorHex, bool stayUntilRead, bool enabled = true)
    {
        _appName = appName;
        _colorHex = colorHex;
        _stayUntilRead = stayUntilRead;
        _enabled = enabled;
    }

    public Color SwatchColor
    {
        get
        {
            var rgb = HexColor.Parse(ColorHex, new HueRgb(0x2E, 0xE6, 0x5B));
            return Color.FromRgb(rgb.R, rgb.G, rgb.B);
        }
    }

    public Brush SwatchBrush => new SolidColorBrush(SwatchColor);
    public string ModeText => StayUntilRead ? "Okunana kadar kalır" : "Yanıp söner";
    public string StatusText => Enabled ? "Etkin" : "Kapalı";

    public NotificationRule ToModel() => new(AppName.Trim(), ColorHex, StayUntilRead, Enabled);

    partial void OnColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(SwatchColor));
        OnPropertyChanged(nameof(SwatchBrush));
    }

    partial void OnStayUntilReadChanged(bool value) => OnPropertyChanged(nameof(ModeText));

    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}
