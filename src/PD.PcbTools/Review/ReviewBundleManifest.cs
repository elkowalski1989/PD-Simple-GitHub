using System.Collections.Immutable;
using System.Globalization;
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

        if (parsed.ImageWidth <= 0 || parsed.ImageHeight <= 0)
        {
            error = "Image dimensions must be positive.";
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ImageEntry entry in parsed.Images)
        {
            if (string.IsNullOrWhiteSpace(entry.FileName))
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

            if (entry.ByteLength < 0 || string.IsNullOrWhiteSpace(entry.Sha256))
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
    public static void WriteFileAtomically(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("The output path needs a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Writes text atomically (UTF-8).</summary>
    public static void WriteTextAtomically(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteFileAtomically(path, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Verifies every manifest image exists beside the manifest and matches its
    /// hash. Returns the verification error, or null when the bundle is intact.
    /// </summary>
    public static string? VerifyImages(string directory, Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
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
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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

    /// <summary>
    /// Retention: deletes only files in the directory that are neither the
    /// manifest nor a manifest-linked image. Returns deleted file names.
    /// </summary>
    public static IReadOnlyList<string> PruneUnlinkedFiles(string directory, Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestFileName };
        foreach (ImageEntry entry in manifest.Images)
        {
            linked.Add(entry.FileName);
        }

        var deleted = new List<string>();
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(path);
            if (linked.Contains(name))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                deleted.Add(name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retention never fails reopen: the orphan stays and is reported by name.
                deleted.Add(name + " (delete failed: " + exception.Message + ")");
            }
        }

        return deleted;
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
        if (fileName.Contains('\\') || fileName.Contains('/') || fileName.Contains(':') ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsafe image file name '{fileName}'.", nameof(fileName));
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the temp name is unique and the next write retries.
        }
    }
}
