using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PD.Simple;

internal static class BackgroundStartupChecks
{
    public static void Run()
    {
        EnsureApplication();
        CheckWindowStartsHiddenAndCanBeRestored();
        CheckResidentFocusSignalIsConsumedOnce();
        Console.WriteLine(
            "PASS: PD Simple starts loaded but hidden and the resident focus signal restores it once.");
    }

    private static void EnsureApplication()
    {
        if (Application.Current is not null)
        {
            return;
        }

        var bootstrap = new App();
        bootstrap.InitializeComponent();
        bootstrap.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    private static void CheckWindowStartsHiddenAndCanBeRestored()
    {
        var window = new MainWindow(Array.Empty<string>());
        try
        {
            window.ShowInBackground();
            Pump(window, DispatcherPriority.Loaded);

            if (!window.IsLoaded || window.IsVisible || window.ShowInTaskbar)
            {
                throw new InvalidOperationException(
                    "The background startup path did not retain a loaded, hidden, taskbar-free window.");
            }
            if (window.FindName("ReconnectButton") is not Button reconnect ||
                !reconnect.IsEnabled)
            {
                throw new InvalidOperationException(
                    "A disconnected PD Simple window disabled its reconnect/attach recovery action.");
            }

            window.ShowFromAllegro();
            Pump(window, DispatcherPriority.ApplicationIdle);
            if (!window.IsVisible || !window.ShowInTaskbar ||
                window.WindowState == WindowState.Minimized)
            {
                throw new InvalidOperationException(
                    "The Allegro activation path did not restore the PD Simple window.");
            }
        }
        finally
        {
            Close(window);
        }
    }

    private static void CheckResidentFocusSignalIsConsumedOnce()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "pd-simple-focus-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string? resolved = BridgeActivationSignal.ResolveBridgeDirectory(
                ["--bridge-dir", root]);
            if (!string.Equals(resolved, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The focus listener did not resolve the launch bridge directory.");
            }

            DateTime processStart = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
            var signal = new BridgeActivationSignal(processStart);
            signal.ObserveSession(root);
            if (signal.TryConsume())
            {
                throw new InvalidOperationException(
                    "A missing resident focus signal was treated as an activation request.");
            }

            string path = Path.Combine(root, "pd-simple-focus.signal");
            File.WriteAllText(path, "PD_SIMPLE_FOCUS_V1\n");
            File.SetLastWriteTimeUtc(path, processStart.AddSeconds(1));
            if (!signal.TryConsume() || signal.TryConsume())
            {
                throw new InvalidOperationException(
                    "The resident focus signal was not consumed exactly once.");
            }
            if (File.Exists(path))
            {
                throw new InvalidOperationException(
                    "The focus listener did not acknowledge activation by removing its private signal.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Close(MainWindow window)
    {
        window.Close();
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (window.IsLoaded && DateTime.UtcNow < deadline)
        {
            Pump(window, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }

        if (window.IsLoaded)
        {
            throw new InvalidOperationException(
                "The background startup check could not close MainWindow within 30 seconds.");
        }
    }

    private static void Pump(MainWindow window, DispatcherPriority priority) =>
        window.Dispatcher.Invoke(static () => { }, priority);
}
