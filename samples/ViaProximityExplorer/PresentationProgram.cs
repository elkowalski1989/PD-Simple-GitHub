using System.Windows;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Queries;
using CircuitHub.AllegroBridge.Wpf.Engine;

namespace ViaProximityExplorerSample;

internal static class PresentationProgram
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "Live WPF presentation requires Windows. Use ViaProximityExplorer --scene for cross-platform analysis.");
            return 1;
        }

        using var lifetime = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };
        Console.CancelKeyPress += cancel;

        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        StartupEventHandler startup = async (_, _) =>
        {
            int exitCode;
            try
            {
                exitCode = await RunAsync(
                    arguments,
                    application.Dispatcher,
                    lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine(
                    "Acquisition or presentation was canceled. No mutation or replay was requested.");
                exitCode = 2;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                exitCode = 1;
            }
            application.Shutdown(exitCode);
        };
        application.Startup += startup;
        try
        {
            return application.Run();
        }
        finally
        {
            application.Startup -= startup;
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<int> RunAsync(
        string[] arguments,
        Dispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (arguments is ["--help"])
        {
            ExplorerReport.PrintUsage(presentationOnly: true);
            return 0;
        }
        if (!ExplorerRequest.TryParse(
                arguments,
                out ExplorerRequest? request,
                out string error))
        {
            Console.Error.WriteLine(error);
            ExplorerReport.PrintUsage(presentationOnly: true);
            return 64;
        }
        if (request!.InputKind == ExplorerInputKind.SavedScene)
        {
            Console.Error.WriteLine(
                "WPF presentation requires a current live capture. Analyze saved scenes with the headless entry point.");
            ExplorerReport.PrintUsage(presentationOnly: true);
            return 64;
        }

        ProximitySpecification planningSpecification = request.CreateSpecification(
            Guid.NewGuid());
        ProximityQueryRequirements requirements = ProximityQuery.GetRequirements(
            planningSpecification);

        await using AllegroEngineSession session = AllegroEngineSession.Create(
            new EngineSessionOptions
            {
                RequiredCapabilities =
                [
                    EngineCapabilities.SceneRead,
                    EngineCapabilities.Presentation
                ]
            });
        LiveDesignScene live = await ExplorerLiveAcquisition.ReadAsync(
            session,
            request,
            requirements,
            cancellationToken);
        ProximitySpecification specification = request.CreateSpecification(
            live.Scene.Identity.CaptureId);
        ProximityAnalysis analysis = ProximityQuery.Execute(
            live.Scene,
            specification,
            cancellationToken);

        dispatcher.VerifyAccess();
        await using EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
            session,
            dispatcher);
        if (!ReferenceEquals(presentation.Session, session))
        {
            throw new InvalidOperationException(
                "WPF presentation did not retain the one supplied Engine session.");
        }
        await presentation.PresentAsync(
            live,
            analysis.Annotations,
            cancellationToken);
        ExplorerReport.Write(request, live.Scene, analysis, presented: true);
        Console.Error.WriteLine(
            "Live proximity annotations are visible. Press Ctrl+C to clear them and disconnect.");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        presentation.Clear();
        return analysis.State == ProximityQueryState.Complete ? 0 : 3;
    }
}
