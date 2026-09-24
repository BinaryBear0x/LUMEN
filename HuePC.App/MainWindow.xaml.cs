using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HuePC.App.Controls;
using HuePC.App.ViewModels;
using HuePC.Bluetooth.Connection;
using HuePC.Bluetooth.Discovery;
using HuePC.Infrastructure.Logging;
using HuePC.Infrastructure.Persistence;
using Wpf.Ui.Controls;

namespace HuePC.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;
    private readonly HuePC.Core.Interfaces.IDiagnosticLogger _logger;
    private readonly SolidColorBrush _bulbFill;
    private readonly SolidColorBrush _bulbGlow;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _pomodoroItem;
    private bool _exitRequested;
    private bool _trayHintShown;
    private Point _ruleDragStart;
    private NotificationRuleViewModel? _ruleDragItem;

    public MainWindow()
    {
        InitializeComponent();

        _bulbFill = (SolidColorBrush)Resources["BulbFill"];
        _bulbGlow = (SolidColorBrush)Resources["BulbGlow"];

        var logger = new LocalDiagnosticLogger();
        _logger = logger;
        _viewModel = new MainWindowViewModel(
            new WindowsBleDiscovery(logger),
            new WindowsBleTransport(logger),
            logger,
            new JsonDeviceAliasStore(),
            new JsonDeviceMemoryStore(),
            new JsonScheduleStore(),
            new JsonAppSettingsStore());
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.MapPickRequested += OnMapPickRequested;
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SetupTrayIcon();
    }

    private void OnMapPickRequested(object? sender, EventArgs e)
    {
        _ = _logger.WriteAsync("Information", "Harita seçici açılıyor.", null);
        var picker = new MapPickerWindow(_viewModel.WeatherLatitude, _viewModel.WeatherLongitude) { Owner = this };
        var result = picker.ShowDialog();
        _ = _logger.WriteAsync("Information", "Harita seçici kapandı.", new { Result = result?.ToString() ?? "null" });
        if (result == true)
        {
            _viewModel.ApplyMapLocation(picker.SelectedLatitude, picker.SelectedLongitude);
        }
    }

    private void OnTimelineItemMoved(object sender, ScheduleMovedEventArgs e) =>
        _ = _viewModel.MoveScheduleFromTimelineAsync(e.Schedule.Id, e.Time, e.DayIndex);

    private void OnProfileColorPick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ProfileColorSlotViewModel slot)
        {
            return;
        }

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(slot.Color.R, slot.Color.G, slot.Color.B)
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            slot.Color = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        }
    }

    private void OnRuleListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _ruleDragStart = e.GetPosition(RuleList);
        _ruleDragItem = (e.OriginalSource as FrameworkElement)?.DataContext as NotificationRuleViewModel;
    }

    private void OnRuleListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _ruleDragItem is null)
        {
            return;
        }

        var position = e.GetPosition(RuleList);
        if (Math.Abs(position.Y - _ruleDragStart.Y) < 14)
        {
            return;
        }

        DragDrop.DoDragDrop(RuleList, _ruleDragItem, DragDropEffects.Move);
    }

    private void OnRuleListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(NotificationRuleViewModel)) is not NotificationRuleViewModel dragged)
        {
            return;
        }

        var target = FindRuleAt(e.GetPosition(RuleList));
        if (target is not null)
        {
            _viewModel.MoveNotificationRule(dragged, target);
        }
    }

    private NotificationRuleViewModel? FindRuleAt(Point point)
    {
        DependencyObject? hit = RuleList.InputHitTest(point) as DependencyObject;
        while (hit is not null)
        {
            if (hit is FrameworkElement element && element.DataContext is NotificationRuleViewModel rule)
            {
                return rule;
            }

            hit = VisualTreeHelper.GetParent(hit);
        }

        return null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.BulbColor):
                AnimateBrush(_bulbFill, _viewModel.BulbColor);
                break;
            case nameof(MainWindowViewModel.BulbGlowColor):
                AnimateBrush(_bulbGlow, _viewModel.BulbGlowColor);
                break;
            case nameof(MainWindowViewModel.IsPomodoroRunning):
                UpdatePomodoroTrayItem();
                break;
        }
    }

    private void UpdatePomodoroTrayItem()
    {
        if (_pomodoroItem is null)
        {
            return;
        }

        _pomodoroItem.Text = _viewModel.IsPomodoroRunning ? "Pomodoro durdur" : "Pomodoro başlat (25/5)";
    }

    private void OnWheelColorChanged(object? sender, Color color) => _viewModel.ApplyWheelColor(color);

    private static void AnimateBrush(SolidColorBrush brush, Color color)
    {
        var animation = new ColorAnimation(color, TimeSpan.FromMilliseconds(450))
        {
            FillBehavior = FillBehavior.HoldEnd
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "LUMEN",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("LUMEN'i aç", null, (_, _) => RestoreWindow());
        _pomodoroItem = new System.Windows.Forms.ToolStripMenuItem("Pomodoro başlat (25/5)", null,
            (_, _) => _viewModel.TogglePomodoroCommand.Execute(null));
        menu.Items.Add(_pomodoroItem);
        menu.Items.Add("Çıkış", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
    }

    internal void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            // Keep running in the background so enabled alarms keep firing.
            e.Cancel = true;
            Hide();
            ShowTrayHint();
            return;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnClosing(e);
    }

    private void ShowTrayHint()
    {
        if (_trayHintShown || _trayIcon is null)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.BalloonTipTitle = "LUMEN arka planda çalışıyor";
        _trayIcon.BalloonTipText = "Alarmlar çalışmaya devam eder. Tamamen çıkmak için tepsi simgesine sağ tıklayıp Çıkış'ı seçin.";
        _trayIcon.ShowBalloonTip(4000);
    }

    internal Task InitializeAsync() => _viewModel.InitializeAsync();

    private async void OnLoaded(object sender, RoutedEventArgs e) => await _viewModel.InitializeAsync();

    private async void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.MapPickRequested -= OnMapPickRequested;
        await _viewModel.DisposeAsync();
    }
}
