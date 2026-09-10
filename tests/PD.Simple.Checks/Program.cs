using CircuitHub.AllegroBridge;
using PD.Simple;
using Admission = PD.Simple.InteractiveRouteRecovery.Admission;

int checks = 0;
foreach (long generation in new long[] { 27, 9183 })
{
    var binding = new AllegroSessionBinding("changed-session-" + generation, generation, 2, "snapshot", "25");
    string RouteJson(string state, bool committed = true, bool recovery = true) =>
        $$"""{"schema":"pd-workflow-control-result-v1","action":"interactive-route","boardGeneration":{{generation}},"state":"{{state}}","committed":{{committed.ToString().ToLowerInvariant()}},"recoveryAvailable":{{recovery.ToString().ToLowerInvariant()}}} """;
    string UndoJson = $$"""{"schema":"pd-workflow-control-result-v1","action":"interactive-route-undo","boardGeneration":{{generation}},"state":"succeeded"}""";
    string preflightRejectedJson = RouteJson("rejected", false, false)
        .Replace("}", ",\"routePreflightRejected\":true}");
    AllegroOperationReceipt Receipt(AllegroOperationState state, string? json) =>
        new(Guid.NewGuid(), "pd.simple.controls.interactive-route", binding, state, "native_result", "test", DateTimeOffset.UtcNow)
        { ResultPayloadJson = json };
    void Route(string name, AllegroOperationState state, string? json, Admission expected, bool current = true)
    {
        var actual = InteractiveRouteRecovery.AdmitRoute(Receipt(state, json), generation, current);
        if (actual != expected)
        {
            throw new InvalidOperationException($"{name}: expected {expected}, received {actual}");
        }
        checks++;
    }
    void Undo(string name, AllegroOperationState state, string? json, bool expected, bool current = true)
    {
        if (InteractiveRouteRecovery.AdmitUndo(Receipt(state, json), generation, current) != expected)
        {
            throw new InvalidOperationException(name);
        }
        checks++;
    }

    Route("verified commit", AllegroOperationState.Complete, RouteJson("succeeded"), Admission.Committed);
    Route("failed committed geometry permits ONLY recovery", AllegroOperationState.Failed, RouteJson("failed"), Admission.RecoveryRequired);
    Route("failed without recovery is uncertain", AllegroOperationState.Failed, RouteJson("failed", true, false), Admission.Uncertain);
    Route("unproven rollback is uncertain", AllegroOperationState.Failed, RouteJson("failed", false, false), Admission.Uncertain);
    Route("SDK Failed does not make native rejection retryable", AllegroOperationState.Failed, RouteJson("rejected", false, false), Admission.Uncertain);
    Route("proven early preflight rejection allows retry", AllegroOperationState.Failed,
        preflightRejectedJson, Admission.NoMutation);
    Route("false preflight flag does not allow retry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"routePreflightRejected\":true", "\"routePreflightRejected\":false"), Admission.Uncertain);
    Route("string preflight flag does not allow retry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"routePreflightRejected\":true", "\"routePreflightRejected\":\"true\""), Admission.Uncertain);
    Route("wrong preflight property does not allow retry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("routePreflightRejected", "preflightRejected"), Admission.Uncertain);
    Route("generic failure cannot claim preflight rejection", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"state\":\"rejected\"", "\"state\":\"failed\""), Admission.Uncertain);
    Route("preflight rejection cannot claim committed geometry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"committed\":false", "\"committed\":true"), Admission.Uncertain);
    Route("preflight rejection cannot claim recovery", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"recoveryAvailable\":false", "\"recoveryAvailable\":true"), Admission.Uncertain);
    Route("wrong board rejects preflight evidence", AllegroOperationState.Failed,
        preflightRejectedJson, Admission.Uncertain, false);
    Route("wrong generation rejects preflight evidence", AllegroOperationState.Failed,
        preflightRejectedJson.Replace($"\"boardGeneration\":{generation}", $"\"boardGeneration\":{generation + 1}"), Admission.Uncertain);
    Route("completed SDK result contradicts preflight rejection", AllegroOperationState.Complete,
        preflightRejectedJson, Admission.Uncertain);
    Route("duplicate preflight flag does not allow retry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"routePreflightRejected\":true",
            "\"routePreflightRejected\":true,\"routePreflightRejected\":false"), Admission.Uncertain);
    Route("mismatched success state cannot certify failed mutation", AllegroOperationState.Failed, RouteJson("succeeded"), Admission.Uncertain);
    Route("cancelled with nonmutation proof", AllegroOperationState.Cancelled, RouteJson("cancelled", false, false), Admission.NoMutation);
    Route("cancelled still committed is uncertain", AllegroOperationState.Cancelled, RouteJson("cancelled"), Admission.Uncertain);
    Route("missing cancelled proof", AllegroOperationState.Cancelled, null, Admission.Uncertain);
    Route("malformed completed proof", AllegroOperationState.Complete, "{", Admission.Uncertain);
    Route("wrong board denies recovery", AllegroOperationState.Failed, RouteJson("failed"), Admission.Uncertain, false);
    Route("wrong generation denies recovery", AllegroOperationState.Failed,
        RouteJson("failed").Replace($"\"boardGeneration\":{generation}", $"\"boardGeneration\":{generation + 1}"), Admission.Uncertain);
    Route("duplicate state denies recovery", AllegroOperationState.Failed,
        RouteJson("failed").Replace("\"state\":\"failed\"", "\"state\":\"failed\",\"state\":\"succeeded\""), Admission.Uncertain);
    Route("wrong action denies recovery", AllegroOperationState.Failed,
        RouteJson("failed").Replace("interactive-route", "another-command"), Admission.Uncertain);
    Undo("verified Undo releases restriction", AllegroOperationState.Complete, UndoJson, true);
    Undo("failed Undo cannot retain retry", AllegroOperationState.Failed, UndoJson, false);
    Undo("superseded Undo cannot retain retry", AllegroOperationState.Superseded, UndoJson, false);
    Undo("cancelled Undo cannot retain retry", AllegroOperationState.Cancelled, UndoJson, false);
    Undo("missing Undo proof", AllegroOperationState.Complete, null, false);
    Undo("wrong Undo board", AllegroOperationState.Complete, UndoJson, false, false);
    Undo("route proof is not Undo proof", AllegroOperationState.Complete, RouteJson("succeeded"), false);
    Undo("rejected Undo proof", AllegroOperationState.Complete, UndoJson.Replace("succeeded", "rejected"), false);
}
Console.WriteLine($"PASS: {checks} mutation-result admission checks; verified recovery-only, uncertain failures, and guarded Undo across changed session/board identities.");
