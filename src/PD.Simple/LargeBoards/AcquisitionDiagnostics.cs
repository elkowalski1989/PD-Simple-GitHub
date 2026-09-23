using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.LargeBoards;

internal sealed record LargeBoardFailure(
    string Code,
    string UserMessage,
    string? Resource,
    long? Limit,
    long? Observed,
    bool? CleanupComplete,
    bool PartialDataWithheld,
    LargeBoardCaptureStaleException? IdentityFenceFailure = null,
    string? DiagnosticDetail = null)
{
    public static LargeBoardFailure From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is EngineBulkCaptureResourceException captureFailure)
        {
            EngineBulkResourceLimitEvidence evidence = captureFailure.Evidence;
            return new(
                "capture_resource_exhausted",
                $"Large-board capture reached the {evidence.Resource} limit " +
                $"({evidence.Observed:N0} observed; {evidence.Limit:N0} allowed). " +
                "Reduce the requested data families or raise the explicit bounded budget. " +
                "No partial capture was retained.",
                evidence.Resource,
                evidence.Limit,
                evidence.Observed,
                evidence.CleanupComplete,
                evidence.PartialDataWithheld);
        }
        if (exception is EngineBulkReplayResourceException replayFailure)
        {
            EngineBulkResourceLimitEvidence evidence = replayFailure.Evidence;
            return new(
                "replay_resource_exhausted",
                $"The selected area reached the {evidence.Resource} replay limit " +
                $"({evidence.Observed:N0} observed; {evidence.Limit:N0} allowed). " +
                "Inspect a smaller area or raise the explicit bounded replay budget. " +
                "No partial scene was shown.",
                evidence.Resource,
                evidence.Limit,
                evidence.Observed,
                evidence.CleanupComplete,
                evidence.PartialDataWithheld);
        }
        if (exception is EngineSceneAcquisitionResourceException sceneFailure)
        {
            EngineSceneAcquisitionResourceLimitEvidence evidence = sceneFailure.Evidence;
            return new(
                "scene_resource_exhausted",
                $"The Engine scene read reached the {evidence.Resource} limit " +
                $"({evidence.Observed:N0} observed; {evidence.Limit:N0} allowed). " +
                "Use the explicit large-board capture workflow or request a bounded area. " +
                "No partial scene was shown.",
                evidence.Resource,
                evidence.Limit,
                evidence.Observed,
                evidence.CleanupComplete,
                evidence.PartialSceneWithheld);
        }
        if (exception is LargeBoardCaptureStaleException stale)
        {
            return new(
                stale.MismatchFields.Any(field => field.StartsWith("frozen_", StringComparison.Ordinal))
                    ? "capture_source_provenance_invalid"
                    : "board_generation_changed",
                exception.Message,
                null,
                null,
                null,
                null,
                true,
                stale);
        }
        if (exception is LargeBoardCaptureIncompleteException)
        {
            return new(
                "capture_incomplete",
                "The capture did not provide complete coverage. No clear result is available; narrow the requested scope or review acquisition diagnostics.",
                "coverage",
                null,
                null,
                null,
                true);
        }
        return new(
            "acquisition_failed",
            "Large-board acquisition failed. No partial result was retained. Review the correlated acquisition receipt before retrying.",
            null,
            null,
            null,
            null,
            true,
            DiagnosticDetail: $"{exception.GetType().FullName}: {exception.Message}");
    }
}

internal static class AcquisitionDiagnosticReceiptFactory
{
    public static AcquisitionDiagnosticReceipt CreateSceneReceipt(
        string correlationId,
        string caller,
        string userAction,
        bool applicationVisible,
        WorkspaceDocumentIdentity? document,
        SceneQuery query,
        string applicationVersion,
        string engineVersion,
        DateTimeOffset startedAt,
        long elapsedMilliseconds,
        AcquisitionTerminalState terminalState,
        LargeBoardFailure? failure,
        string userMessage,
        EngineSceneAcquisitionTiming? sceneTiming = null,
        ImmutableArray<EngineSceneAcquisitionResource> sceneResources = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAction);
        ArgumentNullException.ThrowIfNull(query);
        return new()
        {
            CorrelationId = correlationId,
            Feature = "engine-scene-read",
            Caller = caller,
            UserAction = userAction,
            ApplicationVisible = applicationVisible,
            ApplicationVersion = applicationVersion,
            EngineVersion = engineVersion,
            CaptureTokenFingerprint = string.Empty,
            SessionFingerprint = Fingerprint(document?.SessionId),
            DesignFingerprint = Fingerprint(document?.Design),
            SessionGeneration = document?.SessionGeneration ?? 0,
            BoardGeneration = document?.BoardGeneration ?? 0,
            HostProcessId = document?.ProcessId,
            ProtocolVersion = document?.ProtocolVersion ?? string.Empty,
            QueryKind = query.Kind.ToString(),
            Families = query.Families.Select(value => value.ToString()).ToImmutableArray(),
            CopperKinds = query.CopperKinds.Select(value => value.ToString()).ToImmutableArray(),
            IncludeContours = query.IncludeContours,
            HasRegion = query.Region is not null,
            LayerFingerprints = query.Layers
                .Select(layer => Fingerprint(layer.Value))
                .ToImmutableArray(),
            HasModuleSelector = query.Module is not null,
            MaterializedSceneObjectBudget = query.MaximumObjects,
            CaptureBudgets = null,
            ReplayBudgets = null,
            StartedAt = startedAt,
            CaptureElapsedMilliseconds = elapsedMilliseconds,
            ReplayElapsedMilliseconds = 0,
            TerminalState = terminalState,
            PartialDataWithheld = failure?.PartialDataWithheld ??
                terminalState != AcquisitionTerminalState.Complete,
            CleanupComplete = failure?.CleanupComplete,
            CleanupDisposition = DescribeCleanup(failure?.CleanupComplete),
            ExhaustedResource = failure?.Resource,
            ConfiguredLimit = failure?.Limit,
            ObservedCount = failure?.Observed,
            FailureCode = failure?.Code,
            DiagnosticDetail = failure?.DiagnosticDetail,
            UserMessage = userMessage,
            ScenePhaseTiming = sceneTiming,
            SceneResources = sceneResources.IsDefault ? [] : sceneResources,
        };
    }

    public static AcquisitionDiagnosticReceipt Create(
        LargeBoardCaptureRequest request,
        WorkspaceDocumentIdentity? document,
        LargeBoardCaptureStoreInfo? captureInfo,
        SceneQuery query,
        LargeBoardReplayBudgets? replayBudgets,
        LargeBoardCapturePhaseTiming? captureTiming,
        LargeBoardReplayPhaseTiming? replayTiming,
        string applicationVersion,
        string engineVersion,
        DateTimeOffset startedAt,
        long captureElapsedMilliseconds,
        long replayElapsedMilliseconds,
        AcquisitionTerminalState terminalState,
        bool? cleanupComplete,
        LargeBoardFailure? failure,
        string? userMessage)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);
        LargeBoardCaptureIdentity? captureIdentity = captureInfo?.Identity;
        CorridorFrozenSource? frozen = captureIdentity?.SourceTraversal?.FrozenSource ??
            captureInfo?.SourceTraversal?.FrozenSource;
        CorridorSourceDocument? source = frozen?.SourceDocument;
        LargeBoardCaptureStaleException? identityFence = failure?.IdentityFenceFailure;
        WorkspaceDocumentIdentity? observed = identityFence?.Observed;
        return new()
        {
            CorrelationId = request.CorrelationId,
            Feature = "large-board-capture",
            Caller = request.Caller,
            UserAction = request.UserAction,
            ApplicationVisible = request.ApplicationVisible,
            ApplicationVersion = applicationVersion,
            EngineVersion = engineVersion,
            CaptureTokenFingerprint = Fingerprint(captureIdentity?.CaptureToken),
            SessionFingerprint = Fingerprint(document?.SessionId),
            DesignFingerprint = Fingerprint(document?.Design),
            SessionGeneration = document?.SessionGeneration ?? 0,
            BoardGeneration = document?.BoardGeneration ?? 0,
            HostProcessId = document?.ProcessId,
            ProtocolVersion = document?.ProtocolVersion ?? string.Empty,
            CapturedSessionFingerprint = Fingerprint(captureIdentity?.SessionId),
            CapturedDesignFingerprint = Fingerprint(captureIdentity?.Design),
            CapturedSessionGeneration = captureIdentity?.SessionGeneration,
            CapturedBoardGeneration = captureIdentity?.BoardGeneration,
            CapturedHostProcessId = captureIdentity?.ProcessId,
            CapturedProtocolVersion = captureIdentity?.ProtocolVersion ?? string.Empty,
            IsFrozenSource = frozen is not null,
            SourceSessionFingerprint = Fingerprint(source?.SessionId),
            SourceDesignFingerprint = Fingerprint(source?.Design),
            SourceSessionGeneration = source?.SessionGeneration,
            SourceBoardGeneration = source?.BoardGeneration,
            SourceHostProcessId = source?.ProcessId,
            SourceProtocolVersion = source?.ProtocolVersion ?? string.Empty,
            FrozenObservationFingerprint = Fingerprint(frozen?.ObservationId),
            FrozenInputSha256 = frozen?.InputSha256,
            SourceSnapshotHash = frozen?.SourceSnapshotHash,
            SourceExportedAt = frozen?.ExportedAt,
            ObservedSessionFingerprint = Fingerprint(observed?.SessionId),
            ObservedDesignFingerprint = Fingerprint(observed?.Design),
            ObservedSessionGeneration = observed?.SessionGeneration,
            ObservedBoardGeneration = observed?.BoardGeneration,
            ObservedHostProcessId = observed?.ProcessId,
            IdentityMismatchFields = identityFence?.MismatchFields ?? [],
            CapturePageCount = captureInfo?.PageCount,
            CaptureRecordCount = captureInfo?.RecordCount,
            CaptureStoredBytes = captureInfo?.StoredBytes,
            CaptureResources = captureInfo?.Resources ?? [],
            QueryKind = query.Kind.ToString(),
            Families = query.Families.Select(value => value.ToString()).ToImmutableArray(),
            CopperKinds = query.CopperKinds.Select(value => value.ToString()).ToImmutableArray(),
            IncludeContours = query.IncludeContours,
            HasRegion = query.Region is not null,
            LayerFingerprints = query.Layers
                .Select(layer => Fingerprint(layer.Value))
                .ToImmutableArray(),
            HasModuleSelector = query.Module is not null,
            MaterializedSceneObjectBudget = replayBudgets?.MaximumMaterializedObjects,
            CaptureBudgets = request.Budgets,
            EffectiveCaptureBudgets = captureInfo?.StorageAdmission?.Effective ?? request.Budgets,
            StorageAdmission = captureInfo?.StorageAdmission,
            ReplayBudgets = replayBudgets,
            StartedAt = startedAt,
            CaptureElapsedMilliseconds = captureElapsedMilliseconds,
            ReplayElapsedMilliseconds = replayElapsedMilliseconds,
            TerminalState = terminalState,
            PartialDataWithheld = failure?.PartialDataWithheld ??
                terminalState != AcquisitionTerminalState.Complete,
            CleanupComplete = cleanupComplete,
            CleanupDisposition = DescribeCleanup(cleanupComplete),
            ExhaustedResource = failure?.Resource,
            ConfiguredLimit = failure?.Limit,
            ObservedCount = failure?.Observed,
            FailureCode = failure?.Code,
            DiagnosticDetail = failure?.DiagnosticDetail,
            UserMessage = userMessage,
            CapturePhaseTiming = captureInfo?.Timing ?? captureTiming,
            ReplayPhaseTiming = replayTiming,
        };
    }

    internal static string Fingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 12));
    }

    private static string DescribeCleanup(bool? cleanupComplete) => cleanupComplete switch
    {
        true => "confirmed",
        false => "failed",
        null => "unknown-before-store-publication",
    };
}

internal sealed class JsonLinesAcquisitionDiagnosticOwner : IAcquisitionDiagnosticOwner
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public JsonLinesAcquisitionDiagnosticOwner(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async ValueTask RetainAsync(
        AcquisitionDiagnosticReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }
            string line = JsonSerializer.Serialize(receipt, _json) + Environment.NewLine;
            await File.AppendAllTextAsync(_path, line, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
