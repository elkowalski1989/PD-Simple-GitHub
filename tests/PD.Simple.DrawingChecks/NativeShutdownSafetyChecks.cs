using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PD.Simple;

internal static class NativeShutdownSafetyChecks
{
    internal static void Run()
    {
        EnsureApplication();
        CheckCanceledCloseKeepsWindowUsableWithReason();
        CheckWorkspaceVetoAllowsDockRetryAfterNativeTeardown();
        Console.WriteLine(
            "PASS: native and workspace close vetoes keep PD Simple usable; " +
            "docking can be retried after completed native teardown.");
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

    private static void CheckCanceledCloseKeepsWindowUsableWithReason()
    {
        var window = new MainWindow(Array.Empty<string>());
        try
        {
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Left = -32000;
            window.Top = -32000;
            window.Opacity = 0;
            window.Show();
            Pump(window);

            const string reason = "fake-detach-refusal-shutdown-safety-check";
            window.IsEnabled = false;
            window.CancelCloseForPendingNativeRestore("Close canceled", reason);
            Pump(window);

            Require(window.IsEnabled,
                "The canceled close did not return the window to interactive state.");
            Require(window.WindowState == WindowState.Maximized,
                "The canceled close did not restore a visible maximized docking window.");
            Require(window.StatusText.Text.Contains(reason, StringComparison.Ordinal),
                "The canceled close did not show the specific detach reason in the shell status.");
            Require(window.NativeStatusForChecks.Contains(reason, StringComparison.Ordinal),
                "The canceled close did not show the specific detach reason in the native status.");
            Require(window.NativePaneVisibleForChecks,
                "The canceled close did not keep the native pane visible for retry.");
        }
        finally
        {
            Close(window);
        }
    }

    private static void CheckWorkspaceVetoAllowsDockRetryAfterNativeTeardown()
    {
        var window = new MainWindow(Array.Empty<string>());
        try
        {
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Left = -32000;
            window.Top = -32000;
            window.Opacity = 0;
            window.Show();
            Pump(window);

            // With no borrowed editor, this is the completed native teardown
            // path: it still marks docking unavailable until close resolves.
            window.TeardownNativeDockingAsync().GetAwaiter().GetResult();
            window.IsEnabled = false;
            const string reason = "fake-workspace-close-veto";
            window.CancelCloseForPendingWorkspace("Close canceled", reason);
            Pump(window);

            Require(window.IsEnabled && window.StatusText.Text.Contains(reason, StringComparison.Ordinal),
                "The workspace veto did not return the window to interactive state with its reason.");
            window.NativeDockToggleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(window);
            Require(window.NativeStatusForChecks.Contains(
                    "unavailable until a board is connected", StringComparison.Ordinal),
                "The dock action was still blocked by completed native teardown.");
        }
        finally
        {
            Close(window);
        }
    }

    private static void Close(MainWindow window)
    {
        window.Close();
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (window.IsLoaded && DateTime.UtcNow < deadline)
        {
            Pump(window);
            Thread.Sleep(10);
        }

        if (window.IsLoaded)
        {
            throw new InvalidOperationException(
                "The shutdown-safety check could not close MainWindow within 30 seconds.");
        }
    }

    private static void Pump(MainWindow window)
    {
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Native shutdown-safety check failed: " + message);
        }
    }
}
