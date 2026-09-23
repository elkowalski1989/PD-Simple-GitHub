using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.Simple.Corridor;

/// <summary>
/// A4 run-status checks. The workspace view model must classify success,
/// cancellation, incomplete coverage, and failure into distinct kinds with
/// distinct badge text, and the corridor view must keep that badge visible
/// with Setup closed, with an expandable detail surface and keyboard and
/// automation names.
/// </summary>
internal static class RunStatusChecks
{
    internal static int Run()
    {
        int checks = 0;
        checks += CheckViewModelStates();
        checks += CheckViewBadge();
        checks += CheckCoverageScopeText();
        Console.WriteLine(
            $"PASS: {checks} run status checks (view-model kinds, visible badge, detail, accessibility, coverage scope).");
        return checks;
    }

    private static int CheckViewModelStates()
    {
        int checks = 0;
        AllegroEngineSession session = AllegroEngineSession.Create();
        try
        {
            EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
                session,
                Dispatcher.CurrentDispatcher);
            try
            {
                var workspace = new DpViaCorridorWorkspaceViewModel(session, presentation);
                try
                {
                    workspace.FollowSelection = false;
                    Require(workspace.RunStatusKind == DpViaCorridorRunStatusKind.Idle,
                        "A fresh workspace did not report an idle run status.");
                    Require(workspace.RunStatusText == "Connect to Allegro",
                        $"A fresh workspace badge shows '{workspace.RunStatusText}'.");
                    Require(!workspace.CancelCommand.CanExecute(null),
                        "Cancel is available with no run in flight.");
                    checks += 3;

                    workspace.AdoptResultForTest(CreateAnalysis(complete: true));
                    Require(workspace.RunStatusKind == DpViaCorridorRunStatusKind.Success,
                        "A complete adopted result did not report success.");
                    Require(workspace.RunStatusText == "Previous result \u00B7 3 crossings",
                        $"A disconnected success badge shows '{workspace.RunStatusText}'.");
                    checks += 2;

                    workspace.AdoptResultForTest(CreateAnalysis(complete: false));
                    Require(workspace.RunStatusKind == DpViaCorridorRunStatusKind.Incomplete,
                        "An incomplete adopted result did not report incomplete coverage.");
                    Require(workspace.RunStatusText == "Review required: incomplete inputs",
                        $"An incomplete badge shows '{workspace.RunStatusText}'.");
                    Require(workspace.HasProblem, "Incomplete coverage did not flag a problem.");
                    Require(workspace.EmptyResultsTitle == "Review required: incomplete inputs",
                        $"A blocking empty-results title shows '{workspace.EmptyResultsTitle}'.");
                    Require(workspace.EmptyResultsDetail ==
                        "Required Engine data was unavailable. The absence of displayed " +
                        "crossings is not a pass; read the coverage warnings in the report.",
                        "A blocking empty-results detail changed its unavailable-data wording.");
                    checks += 5;

                    workspace.AdoptResultForTest(CreateAnalysis(complete: false, blockingWarnings: []));
                    Require(workspace.RunStatusKind == DpViaCorridorRunStatusKind.Incomplete,
                        "An advisory-only result did not report review-required coverage.");
                    Require(workspace.RunStatusText == "Review required: 1 coverage warning",
                        $"An advisory badge shows '{workspace.RunStatusText}'.");
                    Require(workspace.StatusTitle == "Review required: 1 coverage warning",
                        $"An advisory status title shows '{workspace.StatusTitle}'.");
                    Require(workspace.HasProblem, "Advisory coverage warnings did not flag a problem.");
                    Require(workspace.EmptyResultsTitle == "Review required: 1 coverage warning",
                        $"An advisory empty-results title shows '{workspace.EmptyResultsTitle}'.");
                    Require(workspace.EmptyResultsTitle == workspace.RunStatusText,
                        "An advisory empty-results title diverged from the badge.");
                    Require(workspace.EmptyResultsDetail ==
                        "1 advisory coverage warning is listed in the report; the gap " +
                        "cannot hide a crossing but review is required. The absence of " +
                        "displayed crossings is not a pass; read the coverage warnings in the report.",
                        $"An advisory empty-results detail shows '{workspace.EmptyResultsDetail}'.");
                    checks += 7;

                    workspace.SimulateRunFailureForTest(
                        "Check did not complete", "simulated acquisition failure");
                    Require(workspace.RunStatusKind == DpViaCorridorRunStatusKind.Failed,
                        "A simulated acquisition failure did not report failure.");
                    Require(workspace.RunStatusText == "Check did not complete",
                        $"A failure badge shows '{workspace.RunStatusText}'.");
                    Require(workspace.StatusDetail == "simulated acquisition failure",
                        "A simulated failure did not retain its detail.");
                    checks += 3;

                    workspace.SimulateRunCancelledForTest();
                    Require(workspace.RunStatusKind == DpViaCorridorRunStatusKind.Cancelled,
                        "A simulated cancellation did not report cancellation.");
                    Require(workspace.RunStatusText == "Check cancelled",
                        $"A cancellation badge shows '{workspace.RunStatusText}'.");
                    Require(!workspace.HasProblem, "A cancellation reads as a failure.");
                    Require(!workspace.CancelCommand.CanExecute(null),
                        "Cancel is available after the run settled.");
                    checks += 4;
                }
                finally
                {
                    workspace.Dispose();
                }
            }
            finally
            {
                presentation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return checks;
    }

    private static int CheckViewBadge()
    {
        int checks = 0;
        if (Application.Current is null)
        {
            var bootstrap = new PD.Simple.App();
            bootstrap.InitializeComponent();
            bootstrap.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        AllegroEngineSession session = AllegroEngineSession.Create();
        try
        {
            EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
                session,
                Dispatcher.CurrentDispatcher);
            try
            {
                var workspace = new DpViaCorridorWorkspaceViewModel(session, presentation);
                try
                {
                    workspace.FollowSelection = false;
                    workspace.SimulateRunFailureForTest(
                        "Check did not complete", "simulated acquisition failure");
                    var view = new DpViaCorridorView();
                    var window = new Window
                    {
                        Content = view,
                        Width = 1100,
                        Height = 750,
                        WindowState = WindowState.Minimized,
                        ShowInTaskbar = false,
                        Left = -32000,
                        Top = -32000,
                        Opacity = 0,
                    };
                    try
                    {
                        window.Show();
                        view.DataContext = workspace;
                        Pump(window);

                        var toggle = (ToggleButton)RequireElement(view, "DpvRunStatusToggle");
                        var badgeText = (TextBlock)RequireElement(view, "DpvRunStatusText");
                        var dot = (System.Windows.Shapes.Ellipse)RequireElement(view, "DpvRunStatusDot");
                        var setupPopup = (Popup)RequireElement(view, "DpvSetupPopup");
                        var statusPopup = (Popup)RequireElement(view, "DpvRunStatusPopup");

                        Require(toggle.Visibility == Visibility.Visible && toggle.IsVisible,
                            "The run status badge is not visible next to Run.");
                        Require(badgeText.Text == "Check did not complete",
                            $"The visible badge shows '{badgeText.Text}' instead of the failure.");
                        Require(!setupPopup.IsOpen,
                            "The failure badge required the Setup popup.");
                        Require(AutomationProperties.GetName(toggle) == "Run status: Check did not complete",
                            "The badge automation name does not carry the failure.");
                        Require(toggle.Focusable && toggle.IsTabStop,
                            "The badge is not keyboard reachable.");
                        Require(DotColor(dot) == Color.FromRgb(0xC4, 0x3E, 0x36),
                            "The failure badge dot is not the failure color.");
                        checks += 6;

                        Require(!statusPopup.IsOpen, "The run detail opened uninvited.");
                        toggle.IsChecked = true;
                        Pump(window);
                        Require(statusPopup.IsOpen, "The badge toggle did not expand run detail.");
                        var detailBox = (TextBox)RequireElement(view, "DpvRunStatusDetailBox");
                        Require(detailBox.Text == "simulated acquisition failure",
                            "The expanded run detail does not carry the failure diagnostics.");
                        Require(detailBox.IsReadOnly, "The run detail is editable.");
                        toggle.IsChecked = false;
                        Pump(window);
                        checks += 4;

                        workspace.SimulateRunCancelledForTest();
                        Pump(window);
                        Require(badgeText.Text == "Check cancelled",
                            $"The visible badge shows '{badgeText.Text}' instead of the cancellation.");
                        Require(AutomationProperties.GetName(toggle) == "Run status: Check cancelled",
                            "The badge automation name does not carry the cancellation.");
                        Require(DotColor(dot) == Color.FromRgb(0x6B, 0x7A, 0x90),
                            "The cancellation badge dot matches another state.");
                        checks += 3;

                        workspace.AdoptResultForTest(CreateAnalysis(complete: false));
                        Pump(window);
                        Require(badgeText.Text == "Review required: incomplete inputs",
                            "The visible badge does not flag incomplete coverage.");
                        Require(DotColor(dot) == Color.FromRgb(0xB8, 0x7A, 0x00),
                            "The incomplete badge dot matches another state.");
                        checks += 2;

                        workspace.AdoptResultForTest(CreateAnalysis(complete: false, blockingWarnings: []));
                        Pump(window);
                        Require(badgeText.Text == "Review required: 1 coverage warning",
                            "The visible badge does not name the advisory warning count.");
                        checks += 1;

                        workspace.AdoptResultForTest(CreateAnalysis(complete: true));
                        Pump(window);
                        Require(badgeText.Text == "Previous result \u00B7 3 crossings",
                            "The visible badge does not report the adopted success.");
                        Require(DotColor(dot) == Color.FromRgb(0x1F, 0x8A, 0x4C),
                            "The success badge dot matches another state.");
                        checks += 2;
                    }
                    finally
                    {
                        window.Close();
                    }
                }
                finally
                {
                    workspace.Dispose();
                }
            }
            finally
            {
                presentation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return checks;
    }

    private static int CheckCoverageScopeText()
    {
        // ResultScope's current-result branch needs a live Engine session,
        // so the coverage selector is exercised directly with blocking,
        // advisory-only, and clean results.
        int checks = 0;
        string blocking = DpViaCorridorWorkspaceViewModel.CurrentResultScopeText(
            CreateAnalysis(complete: false).Result);
        Require(blocking.StartsWith("Incomplete Engine inputs.", StringComparison.Ordinal),
            $"A blocking scope line shows '{blocking}'.");
        checks++;
        string advisory = DpViaCorridorWorkspaceViewModel.CurrentResultScopeText(
            CreateAnalysis(complete: false, blockingWarnings: []).Result);
        Require(advisory ==
            "Review required: 1 advisory coverage warning. The gap cannot hide a " +
            "crossing; findings are review information only and a clear result " +
            "cannot be established. See the report.",
            $"An advisory scope line shows '{advisory}'.");
        checks++;
        string clean = DpViaCorridorWorkspaceViewModel.CurrentResultScopeText(
            CreateAnalysis(complete: true).Result);
        Require(clean.StartsWith("Captured board state,", StringComparison.Ordinal),
            $"A clean scope line shows '{clean}'.");
        checks++;
        return checks;
    }

    private static DpViaCorridorAnalysis CreateAnalysis(
        bool complete,
        IReadOnlyList<string>? blockingWarnings = null)
    {
        var document = new WorkspaceDocumentIdentity(
            "run-status-session",
            SessionGeneration: 1,
            BoardGeneration: 7,
            ProcessId: null,
            Design: @"C:\disposable\runstatus.brd",
            ProtocolVersion: "25");
        DpViaCorridorFinding[] findings = Enumerable.Range(0, 3)
            .Select(index => new DpViaCorridorFinding(
                $"crossing-{index}",
                $"PAIR_{index}",
                $"NET_{index}",
                "cline_segment",
                "ETCH/TOP",
                "Signal",
                "LOW",
                new(100 + index, 100),
                new(140 + index, 100),
                null,
                5 + index,
                12,
                30))
            .ToArray();
        var result = new DpViaCorridorResult(
            DpViaCorridorResult.ManagedSchema,
            "complete",
            7,
            "runstatus",
            "mils",
            "mils",
            "unused.rpt",
            null,
            true,
            3,
            3,
            3,
            3,
            0,
            0,
            3,
            findings)
        {
            CoverageWarnings = complete
                ? Array.Empty<string>()
                : ["simulated missing net"],
            BlockingCoverageWarnings = blockingWarnings,
        };
        return new(document, result);
    }

    private static object RequireElement(DpViaCorridorView view, string name)
    {
        if (view.FindName(name) is { } found)
        {
            return found;
        }
        object? walked = FindDescendant(view, name);
        if (walked is not null)
        {
            return walked;
        }
        throw new InvalidOperationException(
            $"Run status check failed: the view has no element named '{name}'.");
    }

    private static object? FindDescendant(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name == name)
            {
                return child;
            }
            object? found = FindDescendant(child, name);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static Color DotColor(System.Windows.Shapes.Ellipse dot) =>
        dot.Fill is SolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException(
                "Run status check failed: the badge dot has no solid fill.");

    private static void Pump(Window window) =>
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Run status check failed: " + message);
        }
    }
}
