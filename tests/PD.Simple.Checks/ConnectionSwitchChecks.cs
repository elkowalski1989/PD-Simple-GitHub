using System.Text;
using CircuitHub.AllegroBridge;
using PD.Simple;

internal static class ConnectionSwitchChecks
{
    internal static int Run()
    {
        int checks = 0;
        foreach (bool active in new[] { false, true })
        {
            foreach (bool uncertain in new[] { false, true })
            {
                foreach (bool recovery in new[] { false, true })
                {
                    if (active || uncertain || recovery)
                    {
                        RequireThrows<InvalidOperationException>(() =>
                            ConnectionSwitchPolicy.RequireAttachmentAllowed(active, uncertain, recovery));
                    }
                    else
                    {
                        ConnectionSwitchPolicy.RequireAttachmentAllowed(active, uncertain, recovery);
                    }
                    checks++;
                }
            }
        }

        foreach (int processId in new[] { 197, 8201 })
        {
            if (!ConnectionSwitchPolicy.RetainsUndoAuthority(true, processId, 41, processId, 41))
            {
                throw new InvalidOperationException("The same live desktop should preserve guarded Undo.");
            }
            if (ConnectionSwitchPolicy.RetainsUndoAuthority(false, processId, 41, processId, 41) ||
                ConnectionSwitchPolicy.RetainsUndoAuthority(true, processId, 41, processId + 1, 41) ||
                ConnectionSwitchPolicy.RetainsUndoAuthority(true, processId, 41, processId, 42) ||
                ConnectionSwitchPolicy.RetainsUndoAuthority(true, null, 41, processId, 41) ||
                ConnectionSwitchPolicy.RetainsUndoAuthority(true, processId, 0, processId, 0))
            {
                throw new InvalidOperationException("Undo crossed a lost, changed or unproven native desktop.");
            }
            checks += 6;
        }

        foreach (string sessionId in new[] { "alpha-session", "renamed-board-session" })
        {
            var current = new AllegroSessionBinding(sessionId, 17, 1, "first-snapshot", "25");
            var sameBoard = current with { SelectionRevision = 3, SnapshotHash = "later-snapshot" };
            var differentSession = current with { SessionId = sessionId + "-other" };
            var differentGeneration = current with { BoardGeneration = 18 };
            foreach (bool uncertain in new[] { false, true })
            {
                foreach (bool recovery in new[] { false, true })
                {
                    ConnectionSwitchPolicy.RequireSafeSwitch(current, sameBoard, false, uncertain, recovery);
                    checks++;
                    foreach (var next in new[] { sameBoard, differentSession, differentGeneration })
                    {
                        RequireThrows<InvalidOperationException>(() =>
                            ConnectionSwitchPolicy.RequireSafeSwitch(current, next, true, uncertain, recovery));
                        checks++;
                    }
                    foreach (var next in new[] { differentSession, differentGeneration })
                    {
                        if (uncertain || recovery)
                        {
                            RequireThrows<InvalidOperationException>(() =>
                                ConnectionSwitchPolicy.RequireSafeSwitch(current, next, false, uncertain, recovery));
                        }
                        else
                        {
                            ConnectionSwitchPolicy.RequireSafeSwitch(current, next, false, uncertain, recovery);
                        }
                        checks++;
                    }
                }
            }
            ConnectionSwitchPolicy.RequireSafeSwitch(null, current, false, false, false);
            RequireThrows<InvalidOperationException>(() =>
                ConnectionSwitchPolicy.RequireSafeSwitch(null, current, false, true, false));
            checks += 2;
        }

        byte[] text = Encoding.UTF8.GetBytes("abc");
        string hash = SimpleToolExtension.HashSource(text);
        if (hash != "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
        {
            throw new InvalidOperationException("Source identity did not preserve the source bytes.");
        }
        if (SimpleToolExtension.HashSource([0xef, 0xbb, 0xbf, .. text]) != hash)
        {
            throw new InvalidOperationException("A UTF-8 BOM changed the SDK source identity.");
        }
        if (SimpleToolExtension.HashSource(Encoding.UTF8.GetBytes("abc\n")) ==
            SimpleToolExtension.HashSource(Encoding.UTF8.GetBytes("abc\r\n")))
        {
            throw new InvalidOperationException("Source identity silently normalized line endings.");
        }
        RequireThrows<InvalidDataException>(() => SimpleToolExtension.HashSource([]));
        RequireThrows<InvalidDataException>(() => SimpleToolExtension.HashSource([0xef, 0xbb, 0xbf]));
        RequireThrows<InvalidDataException>(() => SimpleToolExtension.HashSource([0x61, 0, 0x62]));
        RequireThrows<DecoderFallbackException>(() => SimpleToolExtension.HashSource([0xc3, 0x28]));
        return checks + 7;
    }

    private static void RequireThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
