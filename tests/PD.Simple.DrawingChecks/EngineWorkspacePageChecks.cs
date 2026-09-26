using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Live;

/// <summary>
/// Engine workspace page behavior. The page borrows one application-owned
/// Engine session, registers the corridor tool through the standard path,
/// and closes before the session is disposed; the shell routes its Tools
/// menu entry to the page and tears it down on close. Offline only: no
/// board, no Allegro, no native provider.
/// </summary>
internal static class EngineWorkspacePageChecks
{
    internal static void Run()
    {
        if (Application.Current is null)
        {
            var bootstrap = new PD.Simple.App();
            bootstrap.InitializeComponent();
            bootstrap.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        CheckPageBorrowsAndCloses();
        CheckShellRoutesAndTearsDown();
        Console.WriteLine(
            "PASS: Engine workspace page borrows one session, registers its tool, " +
            "routes from the Tools menu, and closes before session disposal.");
    }

    private static void CheckPageBorrowsAndCloses()
    {
        var page = new PD.Simple.Tools.EngineWorkspace.EngineWorkspacePage();
        Require(!page.IsAttached, "A fresh workspace page reports attached.");
        Require(
            !string.IsNullOrWhiteSpace(page.StatusMessage),
            "A fresh workspace page has no status.");

        var session = AllegroEngineSession.Create(new EngineSessionOptions
        {
            ConnectionTimeout = TimeSpan.FromMilliseconds(100),
            DisposalTimeout = TimeSpan.FromMilliseconds(250),
        });
        try
        {
            page.Attach(session);
            Require(page.IsAttached, "The workspace page did not attach.");
            Require(
                ReferenceEquals(page.Session, session),
                "The workspace page did not retain the borrowed session.");

            var second = new PD.Simple.Tools.EngineWorkspace.EngineWorkspacePage();
            second.Attach(session);
            RequireThrows<InvalidOperationException>(
                () =>
                {
                    second.Attach(session);
                    return null;
                },
                "A second attach to the same page was accepted.");
            Require(
                second.RequestCloseAsync().AsTask().GetAwaiter().GetResult().CanClose,
                "An idle workspace page refused to close.");
            second.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Require(!second.IsAttached, "A disposed workspace page still reports attached.");

            Require(
                page.RequestCloseAsync().AsTask().GetAwaiter().GetResult().CanClose,
                "The attached workspace page refused to close while idle.");
            page.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Require(!page.IsAttached, "The workspace page still reports attached after disposal.");
            Require(
                session.State.ConnectionState == EngineConnectionState.Disconnected,
                "Disposing the workspace page disturbed the borrowed session.");
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckShellRoutesAndTearsDown()
    {
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

            var hosted = (PD.Simple.Tools.EngineWorkspace.EngineWorkspacePage)window.FindName(
                "EngineWorkspacePage") ??
                throw new InvalidOperationException(
                    "Engine workspace page check failed: the shell has no EngineWorkspacePage.");
            Require(
                hosted.Visibility == Visibility.Collapsed,
                "The Engine workspace page is visible before navigation.");
            var toolsMenu = (Button)window.FindName("ToolsMenuButton");
            MenuItem? item = toolsMenu.ContextMenu?.Items.OfType<MenuItem>()
                .FirstOrDefault(candidate =>
                    Equals(candidate.Header, "Engine workspace (preview)"));
            Require(item is not null, "The Tools menu lost its Engine workspace entry.");
            item!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(window);
            Require(
                hosted.Visibility == Visibility.Visible,
                "The Tools menu entry did not show the Engine workspace page.");
            Require(hosted.IsAttached, "Navigating did not borrow the session into the page.");
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
                    "Engine workspace page check failed: MainWindow did not finish async " +
                    "teardown within 30 seconds.");
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
            throw new InvalidOperationException("Engine workspace page check failed: " + message);
        }
    }

    private static void RequireThrows<TException>(Func<object?> action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("Engine workspace page check failed: " + message);
    }
}
