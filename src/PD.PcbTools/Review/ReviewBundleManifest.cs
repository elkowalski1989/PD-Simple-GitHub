using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Reviews;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PD.PcbTools.Review;

/// <summary>
/// On-disk review bundle written by the Share/review tool. One directory holds
/// a manifest plus the PNG bytes the WPF layer encoded from immutable review
/// pixels. The bundle never contains credentials, license material, live
/// authority tokens, or opaque native handles.
/// </summary>
public static class ReviewBundleManifest
{
    public const string Schema = "pd.review-bundle/v1";
    public const string ManifestFileName = "review-bundle.json";
    public const int MaximumImages = 8;
    public const long MaximumImageBytes = 64 * 1024 * 1024;
    public const int MaximumImageDimension = 16384;
    public const long MaximumImagePixels = 16_777_216;

    /// <summary>Manifest payload stored as review-bundle.json.</summary>
    public sealed record Manifest(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("bundleId")] Guid BundleId,
        [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
        [property: JsonPropertyName("captureId")] Guid? CaptureId,
        [property: JsonPropertyName("document")] string? Document,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("includeAnnotations")] bool IncludeAnnotations,
        [property: JsonPropertyName("imageWidth")] int ImageWidth,
        [property: JsonPropertyName("imageHeight")] int ImageHeight,
        [property: JsonPropertyName("observedAtUtc")] DateTimeOffset? ObservedAtUtc,
        [property: JsonPropertyName("viewport")] string? Viewport,
        [property: JsonPropertyName("qualification")] string? Qualification,
        [property: JsonPropertyName("settings")] ImmutableDictionary<string, string> Settings,
        [property: JsonPropertyName("images")] ImmutableArray<ImageEntry> Images,
        [property: JsonPropertyName("note")] string? Note);

    /// <summary>One PNG member linked by exact filename and SHA-256 hash.</summary>
    public sealed record ImageEntry(
        [property: JsonPropertyName("fileName")] string FileName,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("byteLength")] long ByteLength);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Builds a manifest for PNG members already encoded by the caller.</summary>
    public static Manifest Build(
        Guid bundleId,
        DateTimeOffset createdAtUtc,
        Guid? captureId,
        string? document,
        string mode,
        bool includeAnnotations,
        int imageWidth,
        int imageHeight,
        DateTimeOffset? observedAtUtc,
        string? viewport,
        string? qualification,
        IReadOnlyDictionary<string, string>? settings,
        IReadOnlyList<(string Kind, string FileName, byte[] PngBytes)> images,
        string? note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0 || images.Count > MaximumImages)
        {
            throw new ArgumentOutOfRangeException(nameof(images),
                $"A review bundle holds 1 to {MaximumImages} images.");
        }

        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image dimensions must be positive.");
        }

        var entries = new List<ImageEntry>(images.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string kind, string fileName, byte[] pngBytes) in images)
        {
            string safe = RequireSafeFileName(fileName);
            if (!names.Add(safe))
            {
                throw new ArgumentException($"Duplicate image file name '{safe}'.", nameof(images));
            }

            ArgumentNullException.ThrowIfNull(pngBytes);
            entries.Add(new ImageEntry(safe, kind, Sha256Hex(pngBytes), pngBytes.Length));
        }

        return new Manifest(
            Schema, bundleId, createdAtUtc, captureId, document, mode, includeAnnotations,
            imageWidth, imageHeight, observedAtUtc, viewport, qualification,
            settings is null
                ? ImmutableDictionary<string, string>.Empty
                : [.. settings],
            [.. entries], note);
    }

    /// <summary>Serializes a manifest to its canonical JSON text.</summary>
    public static string Serialize(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    /// <summary>Parses and validates a manifest. Returns the error when invalid.</summary>
    public static bool TryParse(string json, out Manifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        Manifest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            error = "The bundle manifest is not valid JSON: " + exception.Message;
            return false;
        }

        if (parsed is null)
        {
            error = "The bundle manifest is empty.";
            return false;
        }

        if (!string.Equals(parsed.Schema, Schema, StringComparison.Ordinal))
        {
            error = $"Unsupported bundle schema '{parsed.Schema ?? "<missing>"}'; expected '{Schema}'.";
            return false;
        }

        if (parsed.BundleId == Guid.Empty)
        {
            error = "The bundle id is missing.";
            return false;
        }

        if (parsed.Images.IsDefaultOrEmpty || parsed.Images.Length > MaximumImages)
        {
            error = $"A review bundle holds 1 to {MaximumImages} images.";
            return false;
        }

        if (parsed.ImageWidth <= 0 || parsed.ImageHeight <= 0 ||
            parsed.ImageWidth > MaximumImageDimension || parsed.ImageHeight > MaximumImageDimension ||
            (long)parsed.ImageWidth * parsed.ImageHeight > MaximumImagePixels)
        {
            error = "Image dimensions exceed the supported review bounds.";
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ImageEntry entry in parsed.Images)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.FileName))
            {
                error = "An image entry has no file name.";
                return false;
            }

            string safe;
            try
            {
                safe = RequireSafeFileName(entry.FileName);
            }
            catch (ArgumentException exception)
            {
                error = exception.Message;
                return false;
            }

            if (!names.Add(safe))
            {
                error = $"Duplicate image file name '{safe}'.";
                return false;
            }

            if (entry.ByteLength <= 0 || entry.ByteLength > MaximumImageBytes ||
                entry.Sha256 is null || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
            {
                error = $"Image '{safe}' has no hash evidence.";
                return false;
            }
        }

        manifest = parsed;
        return true;
    }

    /// <summary>SHA-256 hex for PNG bytes, computed off the UI thread by the caller.</summary>
    public static string Sha256Hex(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Atomically writes bytes: temp file in the same directory, then move.
    /// A failed write never leaves a partially written file at the target path.
    /// </summary>
    public static void WriteFileAtomically(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        string destination = Path.GetFullPath(path);
        ArchiveFile.RequireNonRedirectedLocalPath(destination);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The output path needs a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        // Engine owns file identity, create-only publication and exact temporary cleanup.
        // Legacy metadata never becomes deletion or overwrite authority.
        ArchiveFile.SaveNewAsync(destination,
            (stream, token) => stream.WriteAsync(bytes.AsMemory(), token), cancellationToken)
            .AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Writes text atomically (UTF-8).</summary>
    public static void WriteTextAtomically(string path, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteFileAtomically(path, Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    /// <summary>
    /// Verifies every manifest image exists beside the manifest and matches its
    /// hash. Returns the verification error, or null when the bundle is intact.
    /// </summary>
    public static string? VerifyImages(string directory, Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!TryParse(Serialize(manifest), out _, out string? manifestError))
        {
            return manifestError;
        }
        foreach (ImageEntry entry in manifest.Images)
        {
            string path = Path.Combine(directory, entry.FileName);
            if (!File.Exists(path))
            {
                return $"Image '{entry.FileName}' is missing from the bundle.";
            }

            byte[] bytes;
            try
            {
                bytes = ReadVerifiedImage(directory, entry);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return $"Image '{entry.FileName}' could not be read: {exception.Message}";
            }

            if (bytes.LongLength != entry.ByteLength || !string.Equals(Sha256Hex(bytes), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"Image '{entry.FileName}' does not match its recorded hash.";
            }
        }

        return null;
    }

    public static byte[] ReadVerifiedImage(string directory, ImageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string name = RequireSafeFileName(entry.FileName);
        string selectedDirectory = Path.GetFullPath(directory);
        string imagePath = Path.GetFullPath(Path.Combine(selectedDirectory, name));
        if (!string.Equals(Path.GetDirectoryName(imagePath), selectedDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("The image is not a sibling of the selected legacy manifest.");
        }
        RequireOrdinaryPath(imagePath);
        if (entry.ByteLength <= 0 || entry.ByteLength > MaximumImageBytes)
        {
            throw new InvalidDataException("The review image exceeds the encoded byte limit.");
        }
        using var stream = new FileStream(imagePath, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        if (stream.Length != entry.ByteLength)
        {
            throw new InvalidDataException($"Image '{name}' does not match its recorded size.");
        }
        byte[] bytes = new byte[checked((int)entry.ByteLength)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || !string.Equals(Sha256Hex(bytes), entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Image '{name}' does not match its recorded hash.");
        }
        return bytes;
    }

    public static void RequireOrdinaryPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Legacy review paths cannot traverse symlinks or reparse points.");
            }
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>Rejects paths, traversal, and reserved names; returns the bare file name.</summary>
    public static string RequireSafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("An image file name is required.", nameof(fileName));
        }

        // Explicit platform-independent separators: a bundle written on Linux
        // must still be safe when reopened on the Windows review host.
        if (fileName.Length > 240 || fileName.Any(char.IsControl) ||
            fileName.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
            fileName.EndsWith(' ') || fileName.EndsWith('.') ||
            fileName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains('\\') || fileName.Contains('/') || fileName.Contains(':') ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsafe image file name '{fileName}'.", nameof(fileName));
        }

        string stem = fileName.Split('.')[0].ToUpperInvariant();
        bool numberedDevice = stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
            (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal));
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || numberedDevice)
        {
            throw new ArgumentException("Reserved image filename.", nameof(fileName));
        }
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            if (fileName.Contains(invalid))
            {
                throw new ArgumentException($"Unsafe image file name '{fileName}'.", nameof(fileName));
            }
        }

        return fileName;
    }

}
