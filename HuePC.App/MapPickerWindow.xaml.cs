using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace HuePC.App;

public partial class MapPickerWindow : FluentWindow
{
    private readonly DispatcherTimer _readoutTimer;

    public MapPickerWindow(double latitude, double longitude)
    {
        InitializeComponent();
        Map.CenterLatitude = latitude;
        Map.CenterLongitude = longitude;
        Map.MarkerLatitude = latitude;
        Map.MarkerLongitude = longitude;
        UpdateReadout();

        _readoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _readoutTimer.Tick += (_, _) => UpdateReadout();
        _readoutTimer.Start();
        Closed += (_, _) => _readoutTimer.Stop();
    }

    public double SelectedLatitude => Map.MarkerLatitude;

    public double SelectedLongitude => Map.MarkerLongitude;

    private void UpdateReadout() =>
        Readout.Text = $"Seçilen konum: {Map.MarkerLatitude:0.0000}, {Map.MarkerLongitude:0.0000} · yakınlaştırma {Map.Zoom}";

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
