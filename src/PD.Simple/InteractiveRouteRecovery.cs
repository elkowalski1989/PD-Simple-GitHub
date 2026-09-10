using System.Text.Json;
using CircuitHub.AllegroBridge;

namespace PD.Simple;

// Typed result admission only. Native ownership and exact Undo stay in SKILL.
internal static class InteractiveRouteRecovery
{
    internal enum Admission
    {
        Committed,
        RecoveryRequired,
        NoMutation,
        Uncertain
    }

    internal static Admission AdmitRoute(AllegroOperationReceipt receipt, long generation, bool sameBoard)
    {
        if (!sameBoard)
        {
            return Admission.Uncertain;
        }
        if (receipt.State == AllegroOperationState.Complete &&
            IsAvailable(receipt.ResultPayloadJson, generation, "succeeded"))
        {
            return Admission.Committed;
        }
        // Native rejection and failure both map to SDK Failed. Only this exact
        // recovery proof permits further mutation, and that mutation is Undo.
        if (receipt.State == AllegroOperationState.Failed &&
            IsAvailable(receipt.ResultPayloadJson, generation, "failed"))
        {
            return Admission.RecoveryRequired;
        }
        // The native producer sets this flag only before admitting a route.
        // A generic or mid-route rejection cannot prove absence of mutation.
        if (receipt.State == AllegroOperationState.Failed &&
            Matches(receipt.ResultPayloadJson, generation, "interactive-route", "rejected", false,
                requirePreflightRejection: true))
        {
            return Admission.NoMutation;
        }
        if (receipt.State == AllegroOperationState.Cancelled &&
            Matches(receipt.ResultPayloadJson, generation, "interactive-route", "cancelled", false))
        {
            return Admission.NoMutation;
        }
        return Admission.Uncertain;
    }

    internal static bool AdmitUndo(AllegroOperationReceipt receipt, long generation, bool sameBoard) =>
        sameBoard && receipt.State == AllegroOperationState.Complete &&
        Matches(receipt.ResultPayloadJson, generation, "interactive-route-undo", "succeeded", null);

    internal static bool IsAvailable(string? json, long boardGeneration, string state)
        => state is "succeeded" or "failed" && Matches(json, boardGeneration, "interactive-route", state, true);

    private static bool Matches(string? json, long boardGeneration, string expectedAction,
        string expectedState, bool? committed, bool requirePreflightRejection = false)
    {
        if (string.IsNullOrWhiteSpace(json) || boardGeneration <= 0)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            var fields = root.EnumerateObject().ToArray();
            if (fields.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            {
                return false;
            }
            bool identityMatches = root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.String &&
                schema.GetString() == "pd-workflow-control-result-v1" &&
                root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String &&
                action.GetString() == expectedAction &&
                root.TryGetProperty("state", out var resultState) && resultState.ValueKind == JsonValueKind.String &&
                resultState.GetString() == expectedState &&
                root.TryGetProperty("boardGeneration", out var generation) && generation.ValueKind == JsonValueKind.Number &&
                generation.TryGetInt64(out var observedGeneration) && observedGeneration == boardGeneration;
            if (!identityMatches)
            {
                return false;
            }
            if (requirePreflightRejection &&
                (!root.TryGetProperty("routePreflightRejected", out var preflightRejected) ||
                    preflightRejected.ValueKind != JsonValueKind.True))
            {
                return false;
            }
            if (committed is null)
            {
                return true;
            }

            var expectedBooleanKind = committed.Value ? JsonValueKind.True : JsonValueKind.False;
            return root.TryGetProperty("committed", out var commitField) &&
                commitField.ValueKind == expectedBooleanKind &&
                root.TryGetProperty("recoveryAvailable", out var recovery) &&
                recovery.ValueKind == expectedBooleanKind;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
