using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PD.Simple.Tools.Analysis;

/// <summary>
/// Lane-local readiness report for one gated action. Ready means every
/// document, data, selection, and authority precondition holds; otherwise
/// <see cref="Reason"/> names the blocker and <see cref="NextStep"/> the
/// corrective action. Lane-local until the coordinator promotes a shared
/// ToolAvailability contract.
/// </summary>
public sealed record ToolReadiness(bool IsReady, string Reason, string NextStep)
{
    public static ToolReadiness Ready(string reason = "All preconditions hold.") =>
        new(true, reason, "Run the action.");

    public static ToolReadiness Blocked(string reason, string nextStep) =>
        new(false, reason, nextStep);
}

/// <summary>
/// Operation-bound result for one Lane A request. Each Run carries its own
/// correlation ID, capture/source identity, timings, and diagnostics; no
/// shared last-result field is used as evidence for another request.
/// </summary>
public sealed record LaneAOperationResult(
    Guid CorrelationId,
    string ToolId,
    string RequestedAction,
    string ExecutedAction,
    Guid? CaptureId,
    string SourceProvenance,
    bool IsSuccess,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    ImmutableArray<string> Diagnostics,
    string? ArtifactPath,
    string? ArtifactSha256)
{
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// Small INPC base for Lane A workspaces. Workspaces are UI-framework free
/// (no WPF references) so offline checks can exercise them on plain .NET.
/// Views observe these properties; every mutation path reports through
/// <see cref="StatusMessage"/> instead of failing silently.
/// </summary>
public abstract class LaneAWorkspaceBase : INotifyPropertyChanged
{
    private bool _isBusy;
    private string _statusMessage = "No input attached.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetField(ref _isBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        protected set => SetField(ref _statusMessage, value ?? string.Empty);
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// View dispatch failures (never Engine evidence) surface in the status
    /// line instead of escaping through async-void handlers.
    /// </summary>
    public void SetStatusFromView(string message) => StatusMessage = message ?? string.Empty;
}

/// <summary>
/// Atomic, hash-recorded file output shared by Lane A export actions.
/// Writes go to a sibling temp file and move into place only after the full
/// payload is flushed; existing destinations are refused unless the caller
/// explicitly opts into overwrite. A partially written file is never
/// advertised as a complete artifact.
/// </summary>
public static class LaneAArtifacts
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
    };

    public static string ComputeSha256(byte[] payload)
    {
        byte[] hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeFileSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<(string Path, string Sha256)> WriteFileAtomicAsync(
        string path,
        byte[] payload,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(payload);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        if (File.Exists(fullPath) && !overwrite)
        {
            throw new IOException(
                $"Refusing to overwrite existing output '{fullPath}' without explicit approval.");
        }
        string staging = fullPath + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(staging, payload, cancellationToken);
            string sha256 = ComputeFileSha256(staging);
            File.Move(staging, fullPath, overwrite);
            return (fullPath, sha256);
        }
        finally
        {
            if (File.Exists(staging))
            {
                try
                {
                    File.Delete(staging);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public static Task<(string Path, string Sha256)> WriteTextAsync(
        string path,
        string content,
        bool overwrite,
        CancellationToken cancellationToken = default) =>
        WriteFileAtomicAsync(path, Encoding.UTF8.GetBytes(content), overwrite, cancellationToken);

    public static Task<(string Path, string Sha256)> WriteJsonAsync<T>(
        string path,
        T value,
        bool overwrite,
        CancellationToken cancellationToken = default) =>
        WriteFileAtomicAsync(
            path,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, ManifestOptions)),
            overwrite,
            cancellationToken);

    public static string FormatHeader(string title, DateTimeOffset when) =>
        $"{title}{Environment.NewLine}Generated (UTC): {when:O}{Environment.NewLine}";
}

/// <summary>
/// Bounded paging over large vertex/contour listings. Callers fetch one
/// explicit page; the total count is always reported so truncation stays
/// visible instead of silently dropping vertices.
/// </summary>
public static class LaneAPaging
{
    public const int DefaultPageSize = 500;
    public const int MaximumPageSize = 1000;

    public static LaneAPage<T> Page<T>(
        ImmutableArray<T> items,
        int pageIndex,
        int pageSize = DefaultPageSize)
    {
        if (pageSize <= 0 || pageSize > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                $"Page size must be between 1 and {MaximumPageSize}; got {pageSize}.");
        }
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "Page index cannot be negative.");
        }
        int total = items.Length;
        int pageCount = total == 0 ? 0 : (total + pageSize - 1) / pageSize;
        if (total == 0 || pageIndex >= pageCount)
        {
            return new LaneAPage<T>(ImmutableArray<T>.Empty, pageIndex, pageSize, pageCount, total);
        }
        int start = pageIndex * pageSize;
        int count = Math.Min(pageSize, total - start);
        ImmutableArray<T> page = items.Slice(start, count);
        return new LaneAPage<T>(page, pageIndex, pageSize, pageCount, total);
    }
}

/// <summary>
/// One explicit page of a larger listing, with totals so callers can see
/// truncation instead of assuming completeness.
/// </summary>
public sealed record LaneAPage<T>(
    ImmutableArray<T> Items,
    int PageIndex,
    int PageSize,
    int PageCount,
    int TotalCount);
