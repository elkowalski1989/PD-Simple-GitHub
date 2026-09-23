using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PD.Simple;
using PD.Simple.Engine;
using PD.Simple.LargeBoards;

internal static class EmbeddedBoardDocumentChecks
{
    internal static int Run()
    {
        int checks = CheckPreferenceRoundTrip();
        checks += CheckShellModeSwitch();
        Console.WriteLine(
            $"PASS: {checks} embedded board-document preference and live shell-mode checks.");
        return checks;
    }

    private static int CheckPreferenceRoundTrip()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "pd-simple-preference-check-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "preferences.json");
        try
        {
            var store = new JsonPdSimplePreferenceStore(path);
            PdSimplePreferences missing = store.Load();
            Require(!missing.EmbeddedBoardDocumentEnabled &&
                    missing.BackgroundCaptureProduct.Length == 0,
                "A missing preference file did not use the safe defaults.");
            store.Save(new PdSimplePreferences
            {
                EmbeddedBoardDocumentEnabled = true,
                BackgroundCaptureProduct = "Allegro_performance",
            });
            PdSimplePreferences saved = store.Load();
            Require(saved.EmbeddedBoardDocumentEnabled &&
                    saved.BackgroundCaptureProduct == "Allegro_performance",
                "The product and embedded choices did not survive a disk round trip.");

            File.WriteAllText(path,
                """{"schemaVersion":1,"embeddedBoardDocumentEnabled":true}""");
            PdSimplePreferences legacy = store.Load();
            Require(legacy.EmbeddedBoardDocumentEnabled &&
                    legacy.BackgroundCaptureProduct.Length == 0,
                "An existing v1 preference file did not load with the product unset.");

            File.WriteAllText(path, "not-json");
            PdSimplePreferences malformed = store.Load();
            Require(!malformed.EmbeddedBoardDocumentEnabled &&
                    malformed.BackgroundCaptureProduct.Length == 0,
                "A malformed preference file did not fail closed.");
            return 4;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static int CheckShellModeSwitch()
    {
        if (Application.Current is null)
        {
            var bootstrap = new App();
            bootstrap.InitializeComponent();
            bootstrap.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var store = new MemoryPreferenceStore(new PdSimplePreferences
        {
            BackgroundCaptureProduct = "Allegro_performance",
        });
        var window = new MainWindow(Array.Empty<string>(), store);
        try
        {
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Left = -32000;
            window.Top = -32000;
            window.Opacity = 0;
            window.Show();
            Pump(window);

            var toggle = Element<ToggleButton>(window, "EmbeddedBoardDocumentToggle");
            var explorer = Element<EngineExplorerView>(window, "ExplorerView");
            var explorerButton = Element<Button>(window, "ExplorerMenuButton");
            var largeBoard = Element<LargeBoardCaptureView>(window, "LargeBoardCaptureView");
            var productInput = Element<TextBox>(window, "BackgroundCaptureProductInput");
            var statusBar = Element<Border>(window, "StatusBar");
            var statusText = Element<TextBlock>(window, "StatusText");
            Require(productInput.Text == "Allegro_performance",
                "The saved background capture product was not shown in the shell.");
            Require(statusBar.Visibility == Visibility.Visible &&
                    statusText.Visibility == Visibility.Visible &&
                    statusBar.MinHeight > 0,
                "Capture-product validation feedback is not visible.");

            productInput.Text = "   ";
            productInput.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Require(store.SaveCount == 0 &&
                    statusText.Text.Contains("Set Background capture product", StringComparison.Ordinal),
                "Whitespace capture product was saved or caused an unhandled focus error.");
            productInput.Text = "Allegro_performance";

            Require(toggle.IsChecked != true && explorer.IsToolPinned,
                "Disabled startup did not retain the pinned tool presentation.");
            Require(explorerButton.Visibility == Visibility.Collapsed &&
                    !largeBoard.EmbeddedBoardDocumentEnabled,
                "Disabled startup exposed the embedded document navigation.");

            toggle.IsChecked = true;
            Pump(window);
            Require(store.Preferences.EmbeddedBoardDocumentEnabled &&
                    store.Preferences.BackgroundCaptureProduct == "Allegro_performance" &&
                    store.SaveCount == 1,
                "Enabling the mode did not persist exactly once.");
            Require(!explorer.IsToolPinned &&
                    explorerButton.Visibility == Visibility.Visible,
                "Enabled mode did not expose the full shared Engine document.");
            Require(explorer.Visibility == Visibility.Visible &&
                    largeBoard.EmbeddedBoardDocumentEnabled,
                "Enabled mode did not navigate to the embedded document surface.");

            toggle.IsChecked = false;
            Pump(window);
            Require(!store.Preferences.EmbeddedBoardDocumentEnabled &&
                    store.Preferences.BackgroundCaptureProduct == "Allegro_performance" &&
                    store.SaveCount == 2,
                "Disabling the mode did not persist exactly once.");
            Require(explorer.IsToolPinned &&
                    explorerButton.Visibility == Visibility.Collapsed &&
                    !largeBoard.EmbeddedBoardDocumentEnabled,
                "Disabled mode did not restore the tool-oriented presentation.");

            productInput.Text = "Allegro_new";
            productInput.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Require(store.Preferences.BackgroundCaptureProduct == "Allegro_new" &&
                    store.SaveCount == 3 &&
                    statusText.Text.Contains("saved", StringComparison.Ordinal),
                "The visible capture-product choice was not saved by the shell.");
            return 11;
        }
        finally
        {
            window.Close();
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (window.IsLoaded && DateTime.UtcNow < deadline)
            {
                window.Dispatcher.Invoke(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);
                Thread.Sleep(10);
            }

            if (window.IsLoaded)
            {
                throw new InvalidOperationException(
                    "Embedded board-document check could not close MainWindow within 30 seconds.");
            }
        }
    }

    private static void Pump(MainWindow window)
    {
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private static T Element<T>(MainWindow window, string name)
        where T : DependencyObject
    {
        if (window.FindName(name) is T found)
        {
            return found;
        }

        throw new InvalidOperationException(
            $"Embedded board-document check found no element named '{name}'.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Embedded board-document check failed: " + message);
        }
    }

    private sealed class MemoryPreferenceStore : IPdSimplePreferenceStore
    {
        internal MemoryPreferenceStore(PdSimplePreferences initial)
        {
            Preferences = initial;
        }

        internal PdSimplePreferences Preferences { get; private set; }

        internal int SaveCount { get; private set; }

        public PdSimplePreferences Load() => Preferences;

        public void Save(PdSimplePreferences preferences)
        {
            Preferences = preferences;
            SaveCount++;
        }
    }
}
