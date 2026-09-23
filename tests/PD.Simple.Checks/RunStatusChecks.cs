using PD.Simple.Corridor;

namespace PD.Simple;

/// <summary>
/// A4 run-status checks. Classification and compact badge text are pure:
/// running, success, cancellation, incomplete coverage, and failure each
/// map to a distinct kind and one-line text, so color is never the only
/// signal. Live WPF rendering still needs GUI acceptance.
/// </summary>
internal static class RunStatusChecks
{
    public static int Run()
    {
        int checks = 0;
        checks += CheckClassification();
        checks += CheckCompactText();
        Console.WriteLine(
            $"PASS: {checks} run status checks (classification, distinct badge text).");
        return checks;
    }

    private static int CheckClassification()
    {
        int checks = 0;
        Require(DpViaCorridorRunStatus.Classify(true, false, false, false, false)
            == DpViaCorridorRunStatusKind.Running, "A busy run did not classify as running.");
        Require(DpViaCorridorRunStatus.Classify(true, true, true, true, true)
            == DpViaCorridorRunStatusKind.Running, "A busy run lost precedence over terminal states.");
        checks += 2;

        Require(DpViaCorridorRunStatus.Classify(false, true, false, false, false)
            == DpViaCorridorRunStatusKind.Cancelled, "A cancelled run did not classify as cancelled.");
        Require(DpViaCorridorRunStatus.Classify(false, true, true, true, true)
            == DpViaCorridorRunStatusKind.Cancelled, "A cancelled run lost precedence over failure and results.");
        checks += 2;

        Require(DpViaCorridorRunStatus.Classify(false, false, true, false, false)
            == DpViaCorridorRunStatusKind.Failed, "A problem run did not classify as failed.");
        Require(DpViaCorridorRunStatus.Classify(false, false, true, true, true)
            == DpViaCorridorRunStatusKind.Failed, "A failure over a stale result did not classify as failed.");
        checks += 2;

        Require(DpViaCorridorRunStatus.Classify(false, false, false, true, true)
            == DpViaCorridorRunStatusKind.Success, "A complete result did not classify as success.");
        Require(DpViaCorridorRunStatus.Classify(false, false, false, true, false)
            == DpViaCorridorRunStatusKind.Incomplete, "An incomplete result did not classify as incomplete.");
        Require(DpViaCorridorRunStatus.Classify(false, false, false, false, false)
            == DpViaCorridorRunStatusKind.Idle, "An empty workspace did not classify as idle.");
        checks += 3;
        return checks;
    }

    private static int CheckCompactText()
    {
        int checks = 0;
        var texts = new HashSet<string>(StringComparer.Ordinal)
        {
            DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Running, "Checking in Allegro", 0, true, false),
            DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Cancelled, "Check cancelled", 0, true, false),
            DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Failed, "Check did not complete", 0, true, false),
            DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Success, "Crossings ready to review", 250, true, true),
            DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Incomplete, "Review required: incomplete inputs", 250, true, false),
            DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Idle, "Ready to check", 0, true, false),
            DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Idle, "Connect to Allegro", 0, false, false),
        };
        Require(texts.Count == 7, "Two run states share the same badge text.");
        checks++;

        Require(DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Running, string.Empty, 0, false, false) == "Checking…",
            "The running badge text changed.");
        Require(DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Cancelled, string.Empty, 0, false, false) == "Check cancelled",
            "The cancelled badge text changed.");
        Require(DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Incomplete, string.Empty, 3, true, false) == "Review required: incomplete inputs",
            "The incomplete badge text changed.");
        Require(DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Success, string.Empty, 250, true, true) == "250 crossings ready",
            "The success badge text does not carry the finding total.");
        checks += 4;

        Require(DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Success, string.Empty, 0, true, true) == "Snapshot: no crossings found",
            "A zero-finding success reads as a populated result.");
        Require(DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Success, string.Empty, 250, true, false) == "Previous result · 250 crossings",
            "A stale success reads as a current result.");
        Require(DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Failed, "Could not start check", 0, false, false) == "Could not start check",
            "A failure badge does not echo its title.");
        Require(DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Idle, string.Empty, 0, true, false) == "Ready to check",
            "An idle connected workspace does not read ready.");
        Require(DpViaCorridorRunStatus.CompactText(DpViaCorridorRunStatusKind.Idle, string.Empty, 0, false, false) == "Connect to Allegro",
            "An idle disconnected workspace does not prompt for connection.");
        checks += 5;
        return checks;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Run status check failed: " + message);
        }
    }
}
