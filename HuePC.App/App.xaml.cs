using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace HuePC.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\LUMEN_5F5C775A_SingleInstance";
    private const string ActivationEventName = @"Local\LUMEN_5F5C775A_Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private SplashWindow? _splashWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(false, InstanceMutexName, out var firstInstance);
        if (!firstInstance)
        {
            SignalRunningInstance();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (timedOut || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    if (_splashWindow?.IsVisible == true) _splashWindow.Activate();
                    else if (MainWindow is MainWindow window) window.RestoreWindow();
                });
            },
            null,
            Timeout.Infinite,
            false);

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => LogCrash(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => LogCrash(args.Exception);

        _splashWindow = new SplashWindow();
        _splashWindow.Show();
        var window = new MainWindow();
        MainWindow = window;
        _ = CompleteStartupAsync(window);
    }

    private async Task CompleteStartupAsync(MainWindow window)
    {
        try
        {
            // Let the splash paint before Bluetooth and saved settings begin loading.
            await Dispatcher.Yield(DispatcherPriority.Background);
            var elapsed = Stopwatch.StartNew();
            var initialization = window.InitializeAsync();
            _ = ObserveInitializationAsync(initialization);
            await Task.WhenAny(initialization, Task.Delay(2700));
            var minimumRemaining = 1600 - (int)elapsed.ElapsedMilliseconds;
            if (minimumRemaining > 0) await Task.Delay(minimumRemaining);
        }
        catch (Exception exception)
        {
            LogCrash(exception);
        }

        window.Show();
        _splashWindow?.Close();
        _splashWindow = null;
        window.Activate();
    }

    private static async Task ObserveInitializationAsync(Task initialization)
    {
        try { await initialization; }
        catch (Exception exception) { LogCrash(exception); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void SignalRunningInstance()
    {
        // The first process may still be creating its event when a second launch arrives.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static void LogCrash(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HuePC",
                "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Crash logging must never throw.
        }
    }
}
