using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HuePC.App.ViewModels;

public sealed partial class DiagnosticCheckViewModel : ObservableObject
{
    public DiagnosticCheckViewModel(string name) => Name = name;

    public string Name { get; }
    [ObservableProperty] private string _result = "Bekliyor…";
    [ObservableProperty] private bool _passed;

    public string Glyph => Passed ? "✓" : "✗";
    public System.Windows.Media.Brush GlyphBrush => Passed
        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7A, 0xD8, 0x5B))
        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x4A));

    partial void OnPassedChanged(bool value)
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(GlyphBrush));
    }
}

public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private string _diagnosticsStatusText = "Bağlantıyı sınamak için “Tanılamayı çalıştır” düğmesine basın.";
    [ObservableProperty] private string _diagnosticLogFilter = "Tümü";

    public ObservableCollection<DiagnosticCheckViewModel> DiagnosticChecks { get; } = [];
    public ObservableCollection<string> DiagnosticLogLines { get; } = [];
    public IReadOnlyList<string> DiagnosticLogFilters { get; } = ["Tümü", "Hata", "Uyarı", "Bilgi"];
    public string DiagnosticsLogPath => Path.Combine(LogDirectory, "ble-events.jsonl");

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        if (_disposed) return;
        DiagnosticChecks.Clear();
        DiagnosticsStatusText = "Tanılama çalışıyor…";

        var adapter = await _discovery.GetAdapterStatusAsync();
        AddCheck("Bluetooth bağdaştırıcısı", adapter is { IsAvailable: true, IsLowEnergySupported: true, IsBluetoothEnabled: true },
            adapter is { IsBluetoothEnabled: false } ? "Bluetooth kapalı" : adapter.Description);

        var hasDevice = SelectedDevice is not null;
        AddCheck("Ampul seçili", hasDevice, hasDevice ? SelectedDevice!.DisplayName : "Önce Ampuller sayfasından bir ampul seçin");

        var connection = GetSelectedConnection();
        AddCheck("Bağlantı kurulu", connection is not null,
            connection is null ? $"Durum: {SelectedConnectionState}" : "BLE oturumu açık");

        if (connection is not null)
        {
            try
            {
                var refresh = await connection.RefreshLightStateAsync();
                AddCheck("Durum okuma", refresh.IsSuccess, refresh.IsSuccess ? "Ampul durumu okundu" : $"{refresh.Status} {refresh.Error}");
            }
            catch (Exception exception)
            {
                AddCheck("Durum okuma", false, exception.Message);
            }

            try
            {
                var write = await connection.SetPowerAsync(IsLightOn);
                AddCheck("Yazma sınaması", write.IsSuccess, write.IsSuccess ? "Aynı güç durumu yazıldı" : $"{write.Status} {write.Error}");
            }
            catch (Exception exception)
            {
                AddCheck("Yazma sınaması", false, exception.Message);
            }

            var rssi = SelectedDevice?.Info.Rssi ?? -100;
            AddCheck("Sinyal gücü", rssi >= -85, $"{rssi} dBm");
        }

        var passed = DiagnosticChecks.Count(check => check.Passed);
        DiagnosticsStatusText = $"{passed}/{DiagnosticChecks.Count} kontrol başarılı.";
        await LogAsync(passed == DiagnosticChecks.Count ? "Information" : "Warning", "Tanılama çalıştırıldı.", new { Passed = passed, Total = DiagnosticChecks.Count });
    }

    private void AddCheck(string name, bool passed, string result)
    {
        DiagnosticChecks.Add(new DiagnosticCheckViewModel(name) { Passed = passed, Result = result });
    }

    [RelayCommand]
    private void RefreshDiagnosticsLog()
    {
        DiagnosticLogLines.Clear();
        try
        {
            if (!File.Exists(DiagnosticsLogPath))
            {
                DiagnosticsStatusText = "Günlük dosyası henüz oluşmamış.";
                return;
            }

            var lines = File.ReadLines(DiagnosticsLogPath).TakeLast(400);
            foreach (var line in lines)
            {
                if (!MatchesLogFilter(line))
                {
                    continue;
                }

                DiagnosticLogLines.Add(line.Length > 320 ? line[..320] + "…" : line);
            }

            while (DiagnosticLogLines.Count > 200)
            {
                DiagnosticLogLines.RemoveAt(0);
            }
        }
        catch (Exception exception)
        {
            DiagnosticsStatusText = $"Günlük okunamadı: {exception.Message}";
        }
    }

    private bool MatchesLogFilter(string line) => DiagnosticLogFilter switch
    {
        "Hata" => line.Contains("\"Level\":\"Error\"", StringComparison.Ordinal) || line.Contains("\"Level\":\"Warning\"", StringComparison.Ordinal),
        "Uyarı" => line.Contains("\"Level\":\"Warning\"", StringComparison.Ordinal),
        "Bilgi" => line.Contains("\"Level\":\"Information\"", StringComparison.Ordinal),
        _ => true
    };

    partial void OnDiagnosticLogFilterChanged(string value) => RefreshDiagnosticsLog();

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _ = LogAsync("Warning", "Günlük klasörü açılamadı.", ErrorData(exception));
        }
    }

    [RelayCommand]
    private void OpenBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _ = LogAsync("Warning", "Bluetooth ayarları açılamadı.", ErrorData(exception));
        }
    }
}
