using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Live;
using PD.PcbTools;

namespace PD.Simple.LargeBoards;

internal sealed record LargeBoardCorridorReport(
    string ReportPath,
    string DataPath);

internal static class LargeBoardCorridorReportWriter
{
    public static async Task<LargeBoardCorridorReport> PublishAsync(
        ScalableCorridorScan scan,
        string design,
        string reportDirectory,
        LargeBoardCaptureIdentity captured,
        WorkspaceDocumentIdentity document,
        Func<WorkspaceDocumentIdentity?> currentDocument,
        Action<LargeBoardCorridorReport> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentException.ThrowIfNullOrWhiteSpace(design);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(currentDocument);
        ArgumentNullException.ThrowIfNull(publish);
        if ((scan.SourceTraversal?.FrozenSource is not null ||
                captured.SourceTraversal?.FrozenSource is not null) &&
            scan.SourceTraversal != captured.SourceTraversal)
        {
            throw new InvalidDataException(
                "The corridor scan and capture have different frozen-source provenance. Run the scan again.");
        }
        RequireCurrent();

        Directory.CreateDirectory(reportDirectory);
        string token = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        string reportPath = Path.Combine(reportDirectory, $"dpvc-large-{token}.rpt");
        string dataPath = reportPath + ".json";
        string temporaryReport = reportPath + ".tmp";
        string temporaryData = dataPath + ".tmp";
        bool createdTemporaryReport = false;
        bool createdTemporaryData = false;
        bool movedReport = false;
        bool movedData = false;
        try
        {
            await using (var stream = new FileStream(
                temporaryReport, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                createdTemporaryReport = true;
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(BuildText(scan, design, captured, document).AsMemory(), cancellationToken);
            }
            await using (var stream = new FileStream(
                temporaryData, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                createdTemporaryData = true;
                string data = JsonSerializer.Serialize(new
                {
                    Schema = "pd-scalable-corridor-v1",
                    EvidenceKind = "historical-sealed-capture",
                    LiveClearOrPassVerified = false,
                    Algorithm = CorridorAnalyzer.Algorithm,
                    Design = design,
                    CaptureIdentity = captured,
                    SourceDocument = captured.SourceTraversal?.FrozenSource?.SourceDocument,
                    PublicationDocument = document,
                    NativeUnits = scan.PlanningScene.Document.NativeUnits,
                    scan.Options,
                    scan.PairCount,
                    scan.CorridorCount,
                    scan.HasCompleteInputs,
                    scan.CoverageWarnings,
                    scan.BlockingCoverageWarnings,
                    scan.Findings,
                });
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(data.AsMemory(), cancellationToken);
            }

            RequireCurrent();
            File.Move(temporaryData, dataPath, overwrite: false);
            createdTemporaryData = false;
            movedData = true;
            RequireCurrent();
            File.Move(temporaryReport, reportPath, overwrite: false);
            createdTemporaryReport = false;
            movedReport = true;
            RequireCurrent();
            var report = new LargeBoardCorridorReport(reportPath, dataPath);
            publish(report);
            return report;
        }
        catch
        {
            if (movedReport)
            {
                DeleteIfPresent(reportPath);
            }
            if (movedData)
            {
                DeleteIfPresent(dataPath);
            }
            throw;
        }
        finally
        {
            if (createdTemporaryReport)
            {
                DeleteIfPresent(temporaryReport);
            }
            if (createdTemporaryData)
            {
                DeleteIfPresent(temporaryData);
            }
        }

        void RequireCurrent()
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceDocumentIdentity current = LargeBoardPublicationFence.RequireCurrent(
                captured, currentDocument());
            if (current != document)
            {
                throw new InvalidDataException(
                    "The Engine document changed while writing the corridor report. Run again.");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static string BuildText(
        ScalableCorridorScan scan,
        string design,
        LargeBoardCaptureIdentity captured,
        WorkspaceDocumentIdentity document)
    {
        var text = new StringBuilder();
        text.AppendLine("Differential Pair Via Corridor Screening Report");
        text.AppendLine("Mode: scalable sealed-capture replay");
        text.AppendLine("Evidence: historical sealed capture. Live-current Clear/Pass is not verified; live actions require fresh primary-host validation.");
        text.AppendLine($"Design: {design}");
        text.AppendLine($"Capture host: session={captured.SessionId}; session-generation={captured.SessionGeneration}; board-generation={captured.BoardGeneration}; process={captured.ProcessId}; design={captured.Design}; protocol={captured.ProtocolVersion}");
        text.AppendLine($"Primary source binding: session={document.SessionId}; session-generation={document.SessionGeneration}; board-generation={document.BoardGeneration}; process={document.ProcessId}; design={document.Design}; protocol={document.ProtocolVersion}");
        if (captured.SourceTraversal?.FrozenSource is { } frozen)
        {
            text.AppendLine($"Frozen observation: {frozen.ObservationId}; exported={frozen.ExportedAt:O}; worker-process={frozen.WorkerProcessId}");
            text.AppendLine($"Frozen input SHA256: {frozen.InputSha256}");
            text.AppendLine($"Primary source snapshot hash: {frozen.SourceSnapshotHash} (source provenance; not a live edit epoch)");
        }
        text.AppendLine($"Algorithm: {CorridorAnalyzer.Algorithm}");
        text.AppendLine($"Native units: {scan.PlanningScene.Document.NativeUnits}");
        text.AppendLine($"Pairs: {scan.PairCount}; corridors: {scan.CorridorCount}; findings: {scan.Findings.Count}");
        text.AppendLine(scan.HasCompleteInputs
            ? "Blocking input gaps: none."
            : $"Blocking input gaps: {scan.BlockingCoverageWarnings.Count}.");
        text.AppendLine($"Review warnings: {scan.CoverageWarnings.Count}.");
        text.AppendLine("This is rule-based screening, not SI simulation or clearance sign-off.");
        foreach (string warning in scan.CoverageWarnings)
        {
            text.AppendLine("REVIEW REQUIRED: " + warning);
        }
        text.AppendLine();
        foreach (ScalableCorridorFinding item in scan.Findings)
        {
            CorridorFinding finding = item.Finding;
            text.AppendLine(
                $"{finding.Id} | {finding.Risk} | {finding.Category} | " +
                $"{finding.PairName} | {finding.AggressorNet} | {finding.Layer} | " +
                finding.ObjectType);
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  P=({finding.P.X}, {finding.P.Y}); N=({finding.N.X}, {finding.N.Y}); " +
                $"intrusion=({finding.Intrusion.X}, {finding.Intrusion.Y}); " +
                $"distance={finding.DistanceMils:0.###}; half-width={finding.HalfWidthMils:0.###}"));
        }
        return text.ToString();
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The primary report error remains authoritative. A later cleanup
            // pass may remove an inaccessible temporary file.
        }
    }
}
