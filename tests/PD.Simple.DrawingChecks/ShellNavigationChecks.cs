using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// B2 shell navigation behavior. Every sidebar destination routes to its
/// tool surface on a real offline MainWindow; duplicate Tools-menu entries
/// share the same handlers; ROUTE actions report honestly offline; and no
/// placeholder-styled entry is operable. Replaces the retired H-SHELL-* and
/// H-NAV-*-01 source-text checks with WPF behavior.
/// </summary>
internal static class ShellNavigationChecks
{
    internal static int Run()
    {
        int checks = 0;
        if (Application.Current is null)
        {
            var bootstrap = new PD.Simple.App();
            bootstrap.InitializeComponent();
            bootstrap.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var window = new PD.Simple.MainWindow(Array.Empty<string>());
        try
        {
            window.WindowState = WindowState.Minimized;
            window.ShowInTaskbar = false;
            window.Left = -32000;
            window.Top = -32000;
            window.Opacity = 0;
            window.Show();
            Pump(window);

            string[] destinations =
            [
                "CrossingMenuButton", "InspectorMenuButton", "MeasureMenuButton", "ScenesMenuButton",
                "OverlayMenuButton", "ReviewMenuButton", "PadstacksMenuButton",
                "ConstraintsDrcMenuButton", "PhysicalSymbolsMenuButton", "ManufacturingMenuButton",
                "PlacementMenuButton", "ViaRouteMenuButton",
            ];
            var buttons = new Dictionary<string, Button>(StringComparer.Ordinal);
            foreach (string name in destinations)
            {
                buttons[name] = Element<Button>(window, name);
            }
            Require(buttons.Count == 12, "A sidebar destination button is missing from the live shell.");
            Require(buttons.Values.All(button => button.IsEnabled),
                "A sidebar destination is disabled on a fresh offline shell.");
            Require(buttons.Values.All(button =>
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))),
                "A sidebar destination has no automation name on the live shell.");
            checks += 3;

            (string Button, string View)[] directTools =
            [
                ("OverlayMenuButton", "OverlayView"),
                ("ReviewMenuButton", "ReviewView"),
                ("PadstacksMenuButton", "PadstacksView"),
                ("ConstraintsDrcMenuButton", "ConstraintsDrcView"),
                ("PhysicalSymbolsMenuButton", "PhysicalSymbolsView"),
                ("ManufacturingMenuButton", "ManufacturingView"),
            ];
            foreach ((string button, string view) in directTools)
            {
                Click(window, buttons[button]);
                Require(VisibilityOf(window, view) == Visibility.Visible,
                    $"Clicking {button} did not show {view}.");
                checks++;
            }

            string[] sections =
            [
                "CrossingMenuButton", "InspectorMenuButton", "MeasureMenuButton",
                "ScenesMenuButton", "PlacementMenuButton", "ViaRouteMenuButton",
            ];
            foreach (string name in sections)
            {
                Click(window, buttons[name]);
                Require(VisibilityOf(window, "ExplorerView") == Visibility.Visible,
                    $"Clicking {name} did not route to the explorer.");
                checks++;
            }
            Require(Element<TextBlock>(window, "BreadcrumbText").Text.Length > 0,
                "Section routing did not set the breadcrumb.");
            checks++;

            Click(window, Element<Button>(window, "HomeMenuButton"));
            Require(VisibilityOf(window, "HomePanel") == Visibility.Visible,
                "Home did not return to the home panel.");
            Click(window, Element<Button>(window, "CorridorMenuButton"));
            Require(VisibilityOf(window, "CorridorView") == Visibility.Visible,
                "The corridor entry did not show the corridor view.");
            Click(window, Element<Button>(window, "RouteMenuButton"));
            Require(VisibilityOf(window, "RoutePanel") == Visibility.Visible,
                "The route entry did not show the route panel.");
            Click(window, Element<Button>(window, "ExplorerMenuButton"));
            Require(VisibilityOf(window, "ExplorerView") == Visibility.Visible,
                "The explorer entry did not show the explorer.");
            checks += 4;

            (string Header, string View)[] menuDuplicates =
            [
                ("Board Explorer", "ExplorerView"),
                ("Point-to-point trace", "RoutePanel"),
                ("Via / route editing", "ExplorerView"),
            ];
            var toolsMenu = Element<Button>(window, "ToolsMenuButton");
            foreach ((string header, string view) in menuDuplicates)
            {
                MenuItem? item = toolsMenu.ContextMenu?.Items.OfType<MenuItem>()
                    .FirstOrDefault(candidate => Equals(candidate.Header, header));
                Require(item is not null, $"The Tools menu lost its '{header}' duplicate entry.");
                item!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Pump(window);
                Require(VisibilityOf(window, view) == Visibility.Visible,
                    $"The Tools menu '{header}' entry did not route like its sidebar button.");
                checks += 2;
            }

            Click(window, Element<Button>(window, "RouteMenuButton"));
            Click(window, Element<Button>(window, "StartRouteButton"));
            string startPhase = Element<TextBlock>(window, "RoutePhaseText").Text;
            Require(startPhase is "Blocked by Engine review" or "Operation unavailable",
                $"An offline route start reported '{startPhase}' instead of a gated outcome.");
            Require(Element<TextBlock>(window, "RouteDetailText").Text.Length > 0,
                "An offline route start left no detail.");
            checks += 2;
            Click(window, Element<Button>(window, "UndoRouteButton"));
            string undoPhase = Element<TextBlock>(window, "RoutePhaseText").Text;
            Require(undoPhase is "Blocked by Engine review" or "Operation unavailable",
                $"An offline route undo reported '{undoPhase}' instead of a gated outcome.");
            checks++;
            // Clear and Cancel are contextual actions. The blocked offline
            // start created neither a first pick nor an active route, so both
            // must remain visible but disabled rather than implying that an
            // operation exists to clear or cancel.
            foreach (string name in new[] { "ClearPickButton", "CancelRouteButton" })
            {
                Button action = Element<Button>(window, name);
                Require(!action.IsEnabled && action.Visibility == Visibility.Visible,
                    $"{name} did not reflect the empty offline route state.");
            }
            checks++;

            Require(Element<Button>(window, "ReconnectButton").IsEnabled,
                "The Reconnect header button is not operable.");
            Require(Element<Button>(window, "ScreenshotButton").IsEnabled,
                "The Screenshot header button is not operable.");
            checks++;

            object? placeholderStyle = window.TryFindResource("FutureNavButton");
            Require(placeholderStyle is Style, "The placeholder button style left the shell resources.");
            int placeholderUsers = Walk(window).OfType<Button>()
                .Count(button => ReferenceEquals(button.Style, placeholderStyle));
            Require(placeholderUsers == 0,
                $"{placeholderUsers} live shell buttons still use the placeholder style.");
            var probe = new Button { Style = (Style)placeholderStyle! };
            Require(!probe.IsEnabled, "The placeholder style no longer gates its users as disabled.");
            checks += 2;
        }
        finally
        {
            window.Close();
            DateTime closeDeadline = DateTime.UtcNow.AddSeconds(30);
            while (window.IsLoaded && DateTime.UtcNow < closeDeadline)
            {
                window.Dispatcher.Invoke(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);
                Thread.Sleep(10);
            }

            if (window.IsLoaded)
            {
                throw new InvalidOperationException(
                    "Shell navigation check failed: MainWindow did not finish async teardown within 30 seconds.");
            }
        }
        Console.WriteLine(
            $"PASS: {checks} shell navigation behavior checks (destinations, duplicates, route gates, placeholders).");
        return checks;
    }

    private static void Click(PD.Simple.MainWindow window, Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
    }

    private static Visibility VisibilityOf(PD.Simple.MainWindow window, string name) =>
        Element<FrameworkElement>(window, name).Visibility;

    private static T Element<T>(PD.Simple.MainWindow window, string name)
        where T : DependencyObject
    {
        if (window.FindName(name) is T found)
        {
            return found;
        }
        throw new InvalidOperationException(
            $"Shell navigation check failed: the shell has no element named '{name}'.");
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            foreach (DependencyObject descendant in Walk(VisualTreeHelper.GetChild(root, index)))
            {
                yield return descendant;
            }
        }
    }

    private static void Pump(PD.Simple.MainWindow window)
    {
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Shell navigation check failed: " + message);
        }
    }
}
