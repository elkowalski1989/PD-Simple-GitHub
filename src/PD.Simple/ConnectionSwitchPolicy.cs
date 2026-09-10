using CircuitHub.AllegroBridge;

namespace PD.Simple;

internal static class ConnectionSwitchPolicy
{
    internal static void RequireAttachmentAllowed(bool operationActive, bool outcomeUncertain,
        bool recoveryRequired)
    {
        if (operationActive || outcomeUncertain || recoveryRequired)
        {
            throw new InvalidOperationException(
                "Finish the active operation and resolve any uncertain result or guarded recovery before attaching. " +
                "Use Reconnect current to refresh the existing connection without discarding its evidence.");
        }
    }

    internal static bool RetainsUndoAuthority(bool previousDesktopCurrent,
        int? previousProcessId, nint previousWindowHandle, int nextProcessId, nint nextWindowHandle) =>
        previousDesktopCurrent && previousProcessId == nextProcessId && previousWindowHandle != 0 &&
        previousWindowHandle == nextWindowHandle;

    internal static void RequireSafeSwitch(AllegroSessionBinding? current,
        AllegroSessionBinding next, bool operationActive, bool outcomeUncertain,
        bool recoveryRequired)
    {
        if (operationActive)
        {
            throw new InvalidOperationException("Finish or cancel the active operation before switching boards.");
        }
        bool sameBoard = current is not null && current.SessionId == next.SessionId &&
            current.BoardGeneration == next.BoardGeneration;
        if (!sameBoard && (outcomeUncertain || recoveryRequired))
        {
            throw new InvalidOperationException(
                "Resolve the current board's uncertain result or guarded recovery before switching boards. " +
                "Reconnect current can refresh its connection without discarding that evidence.");
        }
    }
}
