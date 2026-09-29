using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Reviews;

namespace PD.PcbTools.Review;

/// <summary>PD policy and read-only legacy migration; Bridge owns only the neutral archive.</summary>
public static class PortableReviewPolicy
{
    public static ImmutableDictionary<string, string> DispositionVocabulary { get; } =
        ImmutableDictionary<string, string>.Empty
            .Add("pd:needs-review", "Needs review")
            .Add("pd:confirmed", "Confirmed")
            .Add("pd:dismissed", "Dismissed with reason")
            .Add("pd:resolved-in-later-capture", "Resolved in later capture");

    public static ReviewArchiveContent WithPolicy(ReviewArchiveContent content, JsonElement? reportPolicy = null)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "pd.review-policy/v1",
            dispositions = DispositionVocabulary,
            qualification = "Human review decisions do not waive native DRC or establish analysis completeness.",
            reportPolicy,
        });
        ReviewExtension extension = new("pd.policy", "pd.review-policy/v1", false, [.. json]);
        return content with
        {
            Extensions = content.Extensions.Where(item => item.Namespace != extension.Namespace).Append(extension).ToImmutableArray(),
        };
    }

    public static ReviewArchiveContent ImportLegacy(string manifestPath, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(manifestPath);
        ReviewBundleManifest.RequireOrdinaryPath(fullPath);
        byte[] bytes;
        using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length is < 1 or > 1024 * 1024)
            {
                throw new InvalidDataException("The legacy review manifest exceeds its 1 MiB bound.");
            }
            bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1)
            {
                throw new InvalidDataException("The legacy manifest changed during its bounded read.");
            }
        }
        using JsonDocument json = BoundedDataArchive.ParseJson(bytes);
        string text = Encoding.UTF8.GetString(bytes);
        if (!ReviewBundleManifest.TryParse(text, out ReviewBundleManifest.Manifest? manifest, out string? error))
        {
            throw new InvalidDataException(error);
        }
        ReviewBundleManifest.Manifest legacy = manifest!;
        Guid captureId = legacy.CaptureId is { } recorded && recorded != Guid.Empty ? recorded : Guid.NewGuid();
        string directory = Path.GetDirectoryName(fullPath)!;
        var variants = ImmutableArray.CreateBuilder<ReviewImageVariant>();
        var images = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        long expanded = bytes.Length;
        foreach (ReviewBundleManifest.ImageEntry entry in legacy.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind is not ("raw" or "annotated") || !kinds.Add(entry.Kind))
            {
                throw new InvalidDataException("The legacy image kind is unsupported or duplicated; import did not omit it.");
            }
            byte[] pixels = ReviewBundleManifest.ReadVerifiedImage(directory, entry);
            expanded += pixels.Length;
            if (expanded > 256L * 1024 * 1024)
            {
                throw new InvalidDataException("The legacy review exceeds the aggregate byte budget.");
            }
            (int width, int height) = ReviewArchive.ReadPngDimensions(pixels);
            if (width != legacy.ImageWidth || height != legacy.ImageHeight)
            {
                throw new InvalidDataException("The legacy PNG dimensions differ from its manifest.");
            }
            string member = $"images/{variants.Count + 1:0000}.png";
            images.Add(member, [.. pixels]);
            variants.Add(new(entry.Kind, captureId, entry.Kind == "raw" ? ReviewImageKind.Raw : ReviewImageKind.Annotated,
                ReviewImageAvailability.Available, member, width, height, null)
            {
                MetadataLimitations = ["Legacy viewport text is retained in pd.legacy; no structured viewport was inferred."],
            });
        }
        foreach ((string id, ReviewImageKind kind) in new[] { ("raw", ReviewImageKind.Raw), ("annotated", ReviewImageKind.Annotated) })
        {
            if (!kinds.Contains(id))
            {
                variants.Add(new(id, captureId, kind, ReviewImageAvailability.Unavailable, null, null, null, null,
                    "The selected legacy bundle did not include this variant."));
            }
        }
        var identity = new SceneIdentity(captureId, legacy.ObservedAtUtc ?? legacy.CreatedAtUtc,
            new("pd.review-bundle/v1-import", "1", true, "legacy consistency unrecorded", CaptureAcquisitionKind.Imported));
        var source = new ReviewCaptureSource(identity, null, null, [],
        [
            "The legacy format does not record structured document/query/coverage, analysis findings or package identities.",
            "Original manifest fields and image bytes are retained verbatim in the optional pd.legacy extension and generated image members.",
        ])
        {
            OriginalCaptureIdentityRecorded = legacy.CaptureId is { } recordedId && recordedId != Guid.Empty,
        };
        var review = new ReviewDocument(legacy.BundleId, 1, legacy.CreatedAtUtc, legacy.CreatedAtUtc,
            new("PD legacy review importer", "1", []), [source], [], [], [], [], variants.ToImmutable(),
            [new("legacy_metadata_unknown", "Acquisition coverage, analysis completeness and producer versions were not recorded.", "Warning")],
            legacy.Note);
        ReviewValidation.ValidateDocument(review);
        cancellationToken.ThrowIfCancellationRequested();
        return WithPolicy(new(review, null, images.ToImmutable(), [new("pd.legacy", ReviewBundleManifest.Schema, false, [.. bytes])]));
    }
}
