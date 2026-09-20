using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using CircuitHub.AllegroBridge.Wpf.Engine;

namespace PD.Simple.Diagnostics;

/// <summary>
/// App-owned overlay debug log: transition-triggered receipts (plus a
/// PD-window PNG) written to local files. RenderTargetBitmap-only pixels;
/// no screen capture, no clipboard, no elevation. Logging never throws
/// into the UI: failures are recorded on <see cref="LastError"/> and the
/// capture is skipped.
/// </summary>
internal sealed class OverlayDebugCapture
{
    internal const string Schema = "pd.overlay-debug-receipt/v1";
    internal static readonly TimeSpan MinimumAutoInterval = TimeSpan.FromSeconds(2);
    internal const int RetainedReceipts = 50;
    internal const string DebugDirectoryName = "OverlayDebug";

    private readonly Func<Window?> _windowProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _directoryOverride;
    private readonly object _gate = new();
    private string? _lastAvailability;
    private DateTimeOffset _lastCaptureUtc = DateTimeOffset.MinValue;

    internal string? LastError { get; private set; }

    /// <summary>
    /// Automatic capture on overlay-state changes. Off by default: the
    /// window PNG, hash, receipt write, and pruning run synchronously on
    /// the UI thread, so production navigation leaves this disabled and
    /// timing campaigns enable it only to measure its own contribution.
    /// </summary>
    internal bool AutoCaptureEnabled { get; set; }

    internal OverlayDebugCapture(
        Func<Window?>? windowProvider = null,
        Func<DateTimeOffset>? clock = null,
        string? directoryOverride = null,
        bool autoCaptureEnabled = false)
    {
        _windowProvider = windowProvider is not null
            ? windowProvider
            : () => null;
        _clock = clock is not null
            ? clock
            : () => DateTimeOffset.UtcNow;
        _directoryOverride = directoryOverride;
        AutoCaptureEnabled = autoCaptureEnabled;
    }

    /// <summary>
    /// Writes a receipt (plus PNG when a visible window is available) when
    /// automatic capture is enabled and the overlay availability changed
    /// since the previous call. Returns the receipt, or null when disabled,
    /// nothing changed, the rate limit held, or the capture failed.
    /// </summary>
    internal OverlayDebugReceipt? TryCaptureAuto(
        EngineWpfOverlayState state,
        string? findingId,
        string? reviewBounds)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!AutoCaptureEnabled)
        {
            return null;
        }
        lock (_gate)
        {
            try
            {
                return TryCaptureAutoLocked(state, findingId, reviewBounds);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                return null;
            }
        }
    }

    private OverlayDebugReceipt? TryCaptureAutoLocked(
        EngineWpfOverlayState state,
        string? findingId,
        string? reviewBounds)
    {
        string availability = state.Availability.ToString();
        if (string.Equals(_lastAvailability, availability, StringComparison.Ordinal))
        {
            return null;
        }

        DateTimeOffset now = _clock();
        if (_lastAvailability is not null &&
            now - _lastCaptureUtc < MinimumAutoInterval)
        {
            return null;
        }

        string directory = ResolveDirectory();
        Directory.CreateDirectory(directory);
        string stem = ReceiptFileStem(now, availability);

        string? imagePath = null;
        string? imageSha256 = null;
        Window? window = null;
        try
        {
            window = _windowProvider();
        }
        catch
        {
            window = null;
        }

        if (window is not null && window.IsLoaded && window.IsVisible)
        {
            try
            {
                imagePath = WindowScreenshot.RenderWindowToFile(window, directory, stem);
                imageSha256 = ComputeFileSha256(imagePath);
            }
            catch
            {
                imagePath = null;
                imageSha256 = null;
            }
        }

        var receipt = new OverlayDebugReceipt(
            Schema,
            now,
            "transition",
            availability,
            _lastAvailability,
            state.UnavailableReason,
            state.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray(),
            findingId,
            reviewBounds,
            state.CaptureId,
            state.Revision,
            imagePath is null ? null : Path.GetFileName(imagePath),
            imageSha256,
            AssemblyVersion(typeof(OverlayDebugCapture)),
            AssemblyVersion(typeof(EngineWpfPresentation)));

        string receiptPath = Path.Combine(directory, stem + ".json");
        File.WriteAllText(
            receiptPath,
            JsonSerializer.Serialize(
                receipt,
                new JsonSerializerOptions { WriteIndented = true }));

        PruneDirectory(directory, RetainedReceipts);
        _lastAvailability = availability;
        _lastCaptureUtc = now;
        LastError = null;
        return receipt;
    }

    internal string ResolveDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_directoryOverride))
        {
            return Path.GetFullPath(_directoryOverride);
        }

        return Path.Combine(WindowScreenshot.ResolveOutputDirectory(), DebugDirectoryName);
    }

    internal static string ReceiptFileStem(DateTimeOffset timestamp, string availability)
    {
        string safeAvailability = string.Concat(
            availability.Select(character =>
                char.IsLetterOrDigit(character) ? character : '_'));
        return $"overlay-debug_{timestamp:yyyyMMdd_HHmmss_fff}_{safeAvailability}";
    }

    internal static void PruneDirectory(string directory, int retainedReceipts)
    {
        string[] receipts = Directory
            .EnumerateFiles(directory, "overlay-debug_*.json")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToArray();
        int keep = Math.Max(0, retainedReceipts);
        // Screenshot files carry an extra timestamp beyond the receipt
        // stem, so the retained set comes from the image file names the
        // retained receipts actually record, not from stem matching.
        HashSet<string> retainedImages = new(StringComparer.OrdinalIgnoreCase);
        foreach (string receiptPath in receipts.Take(keep))
        {
            string? imageFile = ReadReceiptImageFileName(receiptPath);
            if (IsContainedImageName(imageFile))
            {
                retainedImages.Add(imageFile!);
            }
        }
        foreach (string stale in receipts.Skip(keep))
        {
            string? staleImage = ReceiptImagePath(directory, stale);
            TryDelete(stale);
            if (staleImage is not null &&
                !retainedImages.Contains(Path.GetFileName(staleImage)))
            {
                TryDelete(staleImage);
            }
        }
        // PNGs whose receipt was never written (image saved, receipt failed)
        // are referenced by no retained receipt and are pruned here.
        foreach (string image in Directory.EnumerateFiles(directory, "overlay-debug_*.png"))
        {
            if (!retainedImages.Contains(Path.GetFileName(image)))
            {
                TryDelete(image);
            }
        }
    }

    private static string? ReadReceiptImageFileName(string receiptPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(receiptPath));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("ImageFileName", out JsonElement image) &&
                image.ValueKind == JsonValueKind.String)
            {
                return image.GetString();
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static bool IsContainedImageName(string? imageFile) =>
        !string.IsNullOrEmpty(imageFile) &&
        string.Equals(imageFile, Path.GetFileName(imageFile), StringComparison.Ordinal);

    private static string? ReceiptImagePath(string directory, string receiptPath)
    {
        string? imageFile = ReadReceiptImageFileName(receiptPath);
        return IsContainedImageName(imageFile)
            ? Path.Combine(directory, imageFile!)
            : null;
    }

    internal static string ComputeFileSha256(string path)
    {
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static string? AssemblyVersion(Type type)
    {
        return type.Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? type.Assembly.GetName().Version?.ToString();
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
