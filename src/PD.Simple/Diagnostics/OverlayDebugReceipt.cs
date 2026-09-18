namespace PD.Simple.Diagnostics;

/// <summary>
/// One overlay debug log entry: the overlay publication evidence PD observed
/// at a transition, plus the PNG the app rendered for it, if any.
/// </summary>
internal sealed record OverlayDebugReceipt(
    string Schema,
    DateTimeOffset TimestampUtc,
    string Trigger,
    string Availability,
    string? PreviousAvailability,
    string? UnavailableReason,
    string[] DiagnosticMessages,
    string? FindingId,
    string? ReviewBounds,
    Guid? CaptureId,
    long? Revision,
    string? ImageFileName,
    string? ImageSha256,
    string? PdVersion,
    string? BridgePackageVersion);
