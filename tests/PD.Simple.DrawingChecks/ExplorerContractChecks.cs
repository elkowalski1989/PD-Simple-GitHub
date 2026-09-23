using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.Simple.Engine;

/// <summary>
/// B2 explorer forwarding behavior. The Engine explorer view borrows one
/// shared Engine/WPF presentation: detached and disposed states refuse
/// with explicit errors, a second attach is refused, and the attached
/// session is retained exactly. Replaces the retired H-SHELL-FWD-01 and
/// H-EXPLORER-SESSION-01 source-text checks with behavior.
/// </summary>
internal static class ExplorerContractChecks
{
    internal static int Run()
    {
        int checks = 0;
        var view = new EngineExplorerView();
        Require(view.Session is null, "A fresh explorer reported a session.");
        Require(view.StatusMessage == "Engine Workbench presentation is not attached.",
            $"A fresh explorer status shows '{view.StatusMessage}'.");
        Require(view.CanClose, "A fresh explorer refused to close.");
        Require(!view.CanStartMutation, "A detached explorer offered a mutation.");
        checks += 4;

        try
        {
            view.OpenSection(WorkbenchSection.Crossings);
            throw new InvalidOperationException(
                "Explorer contract check failed: detached section forwarding did not throw.");
        }
        catch (InvalidOperationException error)
        {
            Require(error.Message == "The shared Engine presentation is not attached.",
                $"Detached forwarding reported '{error.Message}'.");
            checks++;
        }

        try
        {
            view.AttachPresentation(null!);
            throw new InvalidOperationException(
                "Explorer contract check failed: a null presentation attached.");
        }
        catch (ArgumentNullException)
        {
            checks++;
        }

        AllegroEngineSession session = AllegroEngineSession.Create();
        try
        {
            EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
                session,
                Dispatcher.CurrentDispatcher);
            try
            {
                view.AttachPresentation(presentation);
                Require(ReferenceEquals(view.Session, session),
                    "The explorer did not retain the attached presentation session.");
                checks++;
                view.OpenSection(WorkbenchSection.Crossings);
                checks++;
                var boundedScene = EngineExamples.CreateBoard("bounded replay");
                view.ShowScene(boundedScene);
                Require(ReferenceEquals(view.Scene, boundedScene),
                    "The explorer replaced the caller's typed bounded scene.");
                checks++;

                try
                {
                    view.AttachPresentation(presentation);
                    throw new InvalidOperationException(
                        "Explorer contract check failed: a second presentation attached.");
                }
                catch (InvalidOperationException error)
                {
                    Require(error.Message == "The Engine Workbench presentation is already attached.",
                        $"A second attach reported '{error.Message}'.");
                    checks++;
                }

                view.DisposeAsync().AsTask().GetAwaiter().GetResult();
                try
                {
                    view.OpenSection(WorkbenchSection.Crossings);
                    throw new InvalidOperationException(
                        "Explorer contract check failed: section forwarding survived disposal.");
                }
                catch (ObjectDisposedException)
                {
                    checks++;
                }

                try
                {
                    view.HostBusy = true;
                    throw new InvalidOperationException(
                        "Explorer contract check failed: the host-busy flag survived disposal.");
                }
                catch (ObjectDisposedException)
                {
                    checks++;
                }

                try
                {
                    view.IsToolPinned = false;
                    throw new InvalidOperationException(
                        "Explorer contract check failed: the pin flag survived disposal.");
                }
                catch (ObjectDisposedException)
                {
                    checks++;
                }

                try
                {
                    view.ShowScene(boundedScene);
                    throw new InvalidOperationException(
                        "Explorer contract check failed: a scene survived disposal.");
                }
                catch (ObjectDisposedException)
                {
                    checks++;
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
        Console.WriteLine(
            $"PASS: {checks} explorer contract checks (detached/disposed guards, attach, session retention).");
        return checks;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Explorer contract check failed: " + message);
        }
    }
}
