using System.Windows;

// Regression collateral for the native campaign's first two catches: a
// malformed Thickness in ConstraintsDrcView.xaml and a TwoWay binding on the
// read-only ConstraintsDrcViewModel.ExportText. Both crashed PD.Simple at
// startup while every headless gate stayed green, because no maintained check
// ever ran the startup path. Constructing MainWindow (unshown) executes the
// exact offline startup sequence: all hosted tool views, real view-models,
// and binding activation.
internal static class ToolViewsInstantiateChecks
{
    public static void Run()
    {
        // Same bootstrap as CheckWindowScreenshot: App.xaml resources must
        // exist for StaticResource lookups in the views.
        if (Application.Current is null)
        {
            var bootstrap = new PD.Simple.App();
            bootstrap.InitializeComponent();
            bootstrap.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var window = new PD.Simple.MainWindow(Array.Empty<string>());
        try
        {
            // Construction alone only parses XAML. Bindings (including the
            // TwoWay-on-readonly guard) activate at load, so the window is
            // shown invisibly: minimized, off-screen, fully transparent.
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Left = -32000;
            window.Top = -32000;
            window.Opacity = 0;
            window.Show();
            window.Dispatcher.Invoke(
                static () => { },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        finally
        {
            window.Close();
            DateTime closeDeadline = DateTime.UtcNow.AddSeconds(30);
            while (window.IsLoaded && DateTime.UtcNow < closeDeadline)
            {
                window.Dispatcher.Invoke(
                    static () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Thread.Sleep(10);
            }

            if (window.IsLoaded)
            {
                throw new InvalidOperationException(
                    "MainWindow did not finish async teardown within 30 seconds.");
            }
        }

        Console.WriteLine(
            "PASS: MainWindow loads invisibly with all tool views, view-models, " +
            "and bindings activated (offline startup path).");
    }
}
