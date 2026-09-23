using System.IO;

namespace PD.Simple.LargeBoards;

/// <summary>
/// Admission estimate for the temporary volume after a frozen board export.
/// Limits still fail closed if another process consumes disk or an actual
/// capture exceeds a cap. This does not reserve free space.
/// </summary>
internal sealed record LargeBoardStorageAdmission(
    LargeBoardCaptureBudgets Requested,
    LargeBoardCaptureBudgets Effective,
    long AvailableBytes,
    long ReserveBytes,
    long EstimatedPeakBytes)
{
    public bool LimitsReduced => Requested != Effective;
}

internal static class LargeBoardStorageBudgets
{
    internal const long ReserveBytes = 256L * 1024 * 1024;
    private const long Mebibyte = 1024L * 1024;

    public static LargeBoardStorageAdmission ForCurrentTemporaryVolume(
        LargeBoardCaptureBudgets requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        string temporaryRoot = Path.GetPathRoot(Path.GetTempPath()) ??
            throw new IOException("The temporary storage volume is unavailable.");
        long availableBytes = new DriveInfo(temporaryRoot).AvailableFreeSpace;
        return Select(requested, availableBytes);
    }

    internal static LargeBoardStorageAdmission Select(
        LargeBoardCaptureBudgets requested,
        long availableBytes)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (availableBytes <= ReserveBytes)
        {
            throw new IOException(
                "There is insufficient temporary disk space for a frozen board capture.");
        }

        long usableBytes = availableBytes - ReserveBytes;
        long requestedPeak = EstimatedPeak(requested);
        if (requestedPeak <= usableBytes)
        {
            return new(requested, requested, availableBytes, ReserveBytes, requestedPeak);
        }

        decimal scale = (decimal)usableBytes / requestedPeak;
        long spoolBytes = ScaleDown(requested.MaximumNativeSpoolBytes, scale);
        long snapshotBytes = ScaleDown(requested.MaximumNativeSnapshotBytes, scale);
        long engineBytes = ScaleDown(requested.MaximumEngineStoreBytes, scale);
        if (spoolBytes < Mebibyte || snapshotBytes < Mebibyte || engineBytes < Mebibyte)
        {
            throw new IOException(
                "There is insufficient temporary disk space for the minimum frozen capture budgets.");
        }

        var effective = requested with
        {
            MaximumNativeSpoolBytes = spoolBytes,
            MaximumNativeSpoolCharacters = Math.Min(
                requested.MaximumNativeSpoolCharacters, spoolBytes),
            MaximumNativeSnapshotBytes = snapshotBytes,
            MaximumEngineStoreBytes = engineBytes,
        };
        return new(requested, effective, availableBytes, ReserveBytes,
            EstimatedPeak(effective));
    }

    private static long ScaleDown(long requestedBytes, decimal scale) =>
        (long)(requestedBytes * scale / Mebibyte) * Mebibyte;

    private static long EstimatedPeak(LargeBoardCaptureBudgets budgets) =>
        checked(budgets.MaximumNativeSnapshotBytes + Math.Max(
            budgets.MaximumNativeSpoolBytes,
            budgets.MaximumEngineStoreBytes));
}
