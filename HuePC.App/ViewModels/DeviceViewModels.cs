using CommunityToolkit.Mvvm.ComponentModel;
using HuePC.Core.Models;

namespace HuePC.App.ViewModels;

public sealed partial class DiscoveredDeviceViewModel : ObservableObject
{
    [ObservableProperty] private BleDeviceInfo _info;
    [ObservableProperty] private string _alias = string.Empty;

    public DiscoveredDeviceViewModel(BleDeviceInfo info, string? alias = null)
    {
        _info = info;
        _alias = alias ?? string.Empty;
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Alias)
        ? string.IsNullOrWhiteSpace(Info.Name) ? "Philips Hue ampulü" : Info.Name
        : Alias;

    partial void OnInfoChanged(BleDeviceInfo value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnAliasChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}
