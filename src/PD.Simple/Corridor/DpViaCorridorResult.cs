namespace PD.Simple.Corridor;

public sealed record DpViaCorridorPoint(double XMil, double YMil);

public sealed record DpViaCorridorFinding(
    string Id, string PairName, string AggressorNet, string ObjectType, string Layer,
    string Category, string Risk, DpViaCorridorPoint P, DpViaCorridorPoint N,
    DpViaCorridorPoint? Intrusion, double DistanceMil, double HalfWidthMil, double HalfLengthMil);

/// <summary>
/// Board-bound Engine screening results. All geometry is already in mils;
/// SourceUnits is capture provenance only. Findings always carries the
/// complete in-memory finding set: counts, filters, paging, selection, and
/// the on-disk report all describe these same findings.
/// </summary>
public sealed record DpViaCorridorResult(
    string Schema, string Status, long BoardGeneration, string Design, string Units, string SourceUnits,
    string ReportPath, string? NavigatorPath, bool NavigatorWritten,
    int PairCount, int CorridorCount, int AggressorCount, int FindingCount,
    int CriticalCount, int MediumCount, int LowCount,
    IReadOnlyList<DpViaCorridorFinding> Findings)
{
    public IReadOnlyList<string> CoverageWarnings { get; init; } = Array.Empty<string>();
    /// <summary>
    /// Blocking subset of <see cref="CoverageWarnings"/>: gaps that can hide
    /// a crossing. Null when the producer did not separate blocking gaps, so
    /// consumers must treat every warning as blocking. Production runs set
    /// this from the scalable scan; advisory-only runs keep review-required
    /// status while carrying no blocking gap.
    /// </summary>
    public IReadOnlyList<string>? BlockingCoverageWarnings { get; init; }
    public bool HasCompleteInputs => CoverageWarnings.Count == 0;
    public DateTimeOffset? CaptureStartedAt { get; init; }
    public DateTimeOffset? CaptureCompletedAt { get; init; }
    public DateTimeOffset? SourceExportedAt { get; init; }

    public const string ManagedSchema = "pd-dp-via-corridor-managed-v1";
}
