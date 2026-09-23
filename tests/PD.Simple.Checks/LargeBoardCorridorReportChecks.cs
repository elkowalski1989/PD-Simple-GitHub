using CircuitHub.AllegroBridge.Engine.Live;
using PD.PcbTools;
using PD.Simple.LargeBoards;

internal static class LargeBoardCorridorReportChecks
{
    public static async Task<int> RunAsync(
        ScalableCorridorScan scan,
        LargeBoardCaptureIdentity captured)
    {
        var document = new WorkspaceDocumentIdentity(
            captured.SessionId, captured.SessionGeneration, captured.BoardGeneration,
            captured.ProcessId, captured.Design, captured.ProtocolVersion);
        int checks = 0;
        foreach (PublicationStage stage in Enum.GetValues<PublicationStage>())
        {
            checks += await CheckRejectedAsync<OperationCanceledException>(
                scan, captured, document, stage, cancel: true);
            checks += await CheckRejectedAsync<LargeBoardCaptureStaleException>(
                scan, captured, document, stage,
                changed: document with { BoardGeneration = document.BoardGeneration + 1 });
        }

        WorkspaceDocumentIdentity?[] changedDocuments =
        [
            document with { SessionId = "a-different-session" },
            document with { SessionGeneration = document.SessionGeneration + 1 },
            document with { ProcessId = (document.ProcessId ?? 0) + 1 },
            null,
        ];
        foreach (WorkspaceDocumentIdentity? changed in changedDocuments)
        {
            checks += await CheckRejectedAsync<LargeBoardCaptureStaleException>(
                scan, captured, document, PublicationStage.Written, changed: changed);
        }
        checks += await CheckRejectedAsync<InvalidDataException>(
            scan, captured, document, PublicationStage.Written,
            changed: document with { Design = "another-board.brd" });
        checks += await CheckRejectedAsync<InvalidDataException>(
            scan, captured, document, PublicationStage.Written,
            changed: document with { ProtocolVersion = "another-protocol" });
        checks += await CheckCollisionAsync(scan, captured, document, collideData: false);
        checks += await CheckCollisionAsync(scan, captured, document, collideData: true);
        return checks;
    }

    private static async Task<int> CheckRejectedAsync<TException>(
        ScalableCorridorScan scan,
        LargeBoardCaptureIdentity captured,
        WorkspaceDocumentIdentity document,
        PublicationStage stage,
        bool cancel = false,
        WorkspaceDocumentIdentity? changed = null)
        where TException : Exception
    {
        string directory = CreateDirectory();
        string existing = Path.Combine(directory, "existing.rpt");
        const string existingText = "An earlier report must be preserved.";
        try
        {
            await File.WriteAllTextAsync(existing, existingText);
            using var cancellation = new CancellationTokenSource();
            bool intercepted = false;
            bool published = false;
            WorkspaceDocumentIdentity? CurrentDocument()
            {
                if (AtStage(directory, stage))
                {
                    intercepted = true;
                    if (cancel)
                    {
                        cancellation.Cancel();
                    }
                    else
                    {
                        return changed;
                    }
                }
                return document;
            }

            await RequireThrowsAsync<TException>(() => LargeBoardCorridorReportWriter.PublishAsync(
                scan, document.Design!, directory, captured, document, CurrentDocument,
                _ => published = true, cancellation.Token));
            Require(intercepted && !published,
                $"A rejected report reached success publication at {stage}.");
            Require(Directory.GetFiles(directory).SequenceEqual([existing]),
                $"A rejected report retained temporary or promoted artifacts at {stage}.");
            Require(await File.ReadAllTextAsync(existing) == existingText,
                "Rollback changed an earlier report.");
            return 4;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<int> CheckCollisionAsync(
        ScalableCorridorScan scan,
        LargeBoardCaptureIdentity captured,
        WorkspaceDocumentIdentity document,
        bool collideData)
    {
        string directory = CreateDirectory();
        const string existingText = "Another writer owns this destination.";
        string? collision = null;
        bool published = false;
        try
        {
            WorkspaceDocumentIdentity CurrentDocument()
            {
                if (collision is null && AtStage(directory, PublicationStage.Written))
                {
                    string temporary = Directory.GetFiles(directory, "*.rpt.tmp").Single();
                    collision = temporary[..^4] + (collideData ? ".json" : "");
                    File.WriteAllText(collision, existingText);
                }
                return document;
            }

            await RequireThrowsAsync<IOException>(() => LargeBoardCorridorReportWriter.PublishAsync(
                scan, document.Design!, directory, captured, document, CurrentDocument,
                _ => published = true));
            Require(collision is not null && !published,
                "A conflicting destination was overwritten and published.");
            Require(Directory.GetFiles(directory).SequenceEqual([collision!]) &&
                    await File.ReadAllTextAsync(collision!) == existingText,
                "Failed promotion deleted another writer's report or retained partial artifacts.");
            return 3;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool AtStage(string directory, PublicationStage stage)
    {
        string[] files = Directory.GetFiles(directory, "dpvc-large-*");
        return stage switch
        {
            PublicationStage.Written => files.Count(path => path.EndsWith(".tmp", StringComparison.Ordinal)) == 2,
            PublicationStage.DataPromoted => files.Any(path => path.EndsWith(".rpt.tmp", StringComparison.Ordinal)) &&
                                            files.Any(path => path.EndsWith(".json", StringComparison.Ordinal)),
            PublicationStage.ReportPromoted => files.Any(path => path.EndsWith(".rpt", StringComparison.Ordinal)) &&
                                              files.Any(path => path.EndsWith(".json", StringComparison.Ordinal)),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "pd-corridor-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private enum PublicationStage { Written, DataPromoted, ReportPromoted }
}
