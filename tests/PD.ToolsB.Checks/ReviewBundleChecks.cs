using PD.PcbTools.Review;
using CircuitHub.AllegroBridge.Engine.Reviews;

internal static class ReviewBundleChecks
{
    internal static void Run()
    {
        CheckRoundTrip();
        CheckVerifyDetectsTampering();
        CheckAtomicWriteFailureLeavesNoPartial();
        CheckImportedDirectoryRetained();
        CheckCancellationAndBounds();
        CheckUnsafeNamesRejected();
        CheckSchemaRejected();
        CheckPortableLegacyImport();
    }

    private static void CheckPortableLegacyImport()
    {
        string directory = FreshDirectory();
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=");
        var manifest = ReviewBundleManifest.Build(Guid.NewGuid(), DateTimeOffset.UtcNow, null, "unrecorded design",
            "legacy", false, 1, 1, null, "legacy viewport text", null,
            new Dictionary<string, string> { ["application-policy"] = "retain exactly" },
            [("raw", "captured.png", png)], "legacy note");
        string json = ReviewBundleManifest.Serialize(manifest);
        string path = Path.Combine(directory, ReviewBundleManifest.ManifestFileName);
        File.WriteAllText(path, json);
        File.WriteAllBytes(Path.Combine(directory, "captured.png"), png);
        try
        {
            ReviewArchiveContent imported = PortableReviewPolicy.ImportLegacy(path);
            if (imported.Review.Captures[0].Document is not null || imported.Review.Captures[0].Query is not null ||
                imported.Review.Captures[0].OriginalCaptureIdentityRecorded || !imported.Review.Analyses.IsEmpty ||
                imported.Review.Images.Single(item => item.Kind == ReviewImageKind.Annotated).Availability != ReviewImageAvailability.Unavailable)
            {
                throw new InvalidOperationException("Legacy import invented missing source, analysis or variant evidence.");
            }
            using var archive = new MemoryStream();
            ReviewArchive.WriteAsync(archive, imported).AsTask().GetAwaiter().GetResult();
            archive.Position = 0;
            ReviewArchiveContent reopened = ReviewArchive.ReadAsync(archive).AsTask().GetAwaiter().GetResult();
            if (System.Text.Encoding.UTF8.GetString(reopened.Extensions.Single(item => item.Namespace == "pd.legacy").Json.AsSpan()) != json ||
                !reopened.Images.Single().Value.AsSpan().SequenceEqual(png) || File.ReadAllText(path) != json)
            {
                throw new InvalidOperationException("Portable copy changed original legacy metadata or captured image bytes.");
            }
            File.WriteAllText(path, "{\"schema\":\"pd.review-bundle/v1\"," + json.TrimStart()[1..]);
            try
            {
                _ = PortableReviewPolicy.ImportLegacy(path);
                throw new InvalidOperationException("Duplicate legacy JSON property was accepted.");
            }
            catch (InvalidDataException)
            {
            }
            File.WriteAllText(path, json);
            if (OperatingSystem.IsLinux())
            {
                string outside = Path.Combine(Path.GetTempPath(), "pd-review-outside-" + Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(outside, png);
                File.Delete(Path.Combine(directory, "captured.png"));
                File.CreateSymbolicLink(Path.Combine(directory, "captured.png"), outside);
                try
                {
                    _ = PortableReviewPolicy.ImportLegacy(path);
                    throw new InvalidOperationException("A redirected legacy image was accepted.");
                }
                catch (InvalidDataException)
                {
                }
                finally
                {
                    File.Delete(Path.Combine(directory, "captured.png"));
                    File.Delete(outside);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (ReviewBundleManifest.Manifest Manifest, byte[] Raw, byte[] Annotated) SampleBundle()
    {
        byte[] raw = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        byte[] annotated = { 0x89, 0x50, 0x4E, 0x47, 4, 5, 6, 7 };
        var manifest = ReviewBundleManifest.Build(
            Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "session/1/design",
            "live-canvas", includeAnnotations: true, 800, 600, DateTimeOffset.UtcNow,
            "viewport mils", "qualified", new Dictionary<string, string> { ["layers"] = "all" },
            [("raw", "review-raw.png", raw), ("annotated", "review-annotated.png", annotated)],
            "lane B check");
        return (manifest, raw, annotated);
    }

    private static void CheckRoundTrip()
    {
        (ReviewBundleManifest.Manifest manifest, _, _) = SampleBundle();
        string json = ReviewBundleManifest.Serialize(manifest);
        if (!ReviewBundleManifest.TryParse(json, out ReviewBundleManifest.Manifest? parsed, out string? error) ||
            parsed is null)
        {
            throw new InvalidOperationException("Manifest round-trip failed: " + error);
        }

        if (parsed.Images.Length != 2 || parsed.Schema != ReviewBundleManifest.Schema)
        {
            throw new InvalidOperationException("Manifest round-trip lost members.");
        }
    }

    private static void CheckVerifyDetectsTampering()
    {
        string directory = FreshDirectory();
        (ReviewBundleManifest.Manifest manifest, byte[] raw, byte[] annotated) = SampleBundle();
        File.WriteAllBytes(Path.Combine(directory, "review-raw.png"), raw);
        File.WriteAllBytes(Path.Combine(directory, "review-annotated.png"), annotated);
        if (ReviewBundleManifest.VerifyImages(directory, manifest) is not null)
        {
            throw new InvalidOperationException("An intact bundle failed verification.");
        }

        // Tamper one byte: verification must name the file.
        byte[] tampered = (byte[])raw.Clone();
        tampered[^1] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(directory, "review-raw.png"), tampered);
        string? failure = ReviewBundleManifest.VerifyImages(directory, manifest);
        if (failure is null || !failure.Contains("review-raw.png", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Tampered bundle passed verification.");
        }

        // Missing file.
        File.Delete(Path.Combine(directory, "review-annotated.png"));
        if (ReviewBundleManifest.VerifyImages(directory, manifest) is null)
        {
            throw new InvalidOperationException("Bundle with a missing image passed verification.");
        }
    }

    private static void CheckAtomicWriteFailureLeavesNoPartial()
    {
        string directory = FreshDirectory();
        string target = Path.Combine(directory, "review-annotated.png");
        ReviewBundleManifest.WriteFileAtomically(target, new byte[] { 1, 2, 3 });
        if (!File.Exists(target))
        {
            throw new InvalidOperationException("Atomic write produced no file.");
        }

        // Overwrite refusal: the existing complete file must survive the failed second write.
        try
        {
            ReviewBundleManifest.WriteFileAtomically(target, new byte[] { 9 });
            throw new InvalidOperationException("Atomic write overwrote an existing file.");
        }
        catch (IOException)
        {
        }

        if (File.ReadAllBytes(target) is not [1, 2, 3])
        {
            throw new InvalidOperationException("A failed overwrite corrupted the complete file.");
        }

        // No temp fragments remain.
        if (Directory.EnumerateFiles(directory).Any(name => Path.GetFileName(name).StartsWith(".tmp-", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Atomic write left a temp fragment.");
        }
    }

    private static void CheckImportedDirectoryRetained()
    {
        string directory = FreshDirectory();
        (ReviewBundleManifest.Manifest manifest, byte[] raw, byte[] annotated) = SampleBundle();
        File.WriteAllBytes(Path.Combine(directory, "review-raw.png"), raw);
        File.WriteAllBytes(Path.Combine(directory, "review-annotated.png"), annotated);
        string[] sentinels = ["renamed-board.brd", "another-source.cs", "private-notes.txt", "unrelated-image.png"];
        foreach (string name in sentinels)
        {
            File.WriteAllText(Path.Combine(directory, name), "unrelated content");
        }
        string json = ReviewBundleManifest.Serialize(manifest);
        if (!ReviewBundleManifest.TryParse(json, out var imported, out _) ||
            ReviewBundleManifest.VerifyImages(directory, imported!) is not null)
        {
            throw new InvalidOperationException("The imported legacy bundle could not be inspected.");
        }
        foreach (string name in sentinels)
        {
            if (File.ReadAllText(Path.Combine(directory, name)) != "unrelated content")
            {
                throw new InvalidOperationException("Inspecting imported evidence changed an unrelated file.");
            }
        }
    }

    private static void CheckCancellationAndBounds()
    {
        string directory = FreshDirectory();
        string path = Path.Combine(directory, "cancelled.png");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            ReviewBundleManifest.WriteFileAtomically(path, [1, 2, 3], cancelled.Token);
            throw new InvalidOperationException("A pre-commit cancelled save succeeded.");
        }
        catch (OperationCanceledException)
        {
        }
        if (Directory.EnumerateFiles(directory).Any())
        {
            throw new InvalidOperationException("A cancelled save published a file or left temporary output.");
        }
        var manifest = SampleBundle().Manifest;
        foreach (var malformed in new[]
        {
            manifest with { ImageWidth = int.MaxValue },
            manifest with { Images = [manifest.Images[0] with { ByteLength = long.MaxValue }] },
            manifest with { Images = [manifest.Images[0] with { FileName = "../outside.png" }] },
            manifest with { Images = [manifest.Images[0] with { Sha256 = "wrong" }] },
        })
        {
            if (ReviewBundleManifest.TryParse(ReviewBundleManifest.Serialize(malformed), out _, out _))
            {
                throw new InvalidOperationException("Malformed or unbounded review input was accepted.");
            }
        }
    }

    private static void CheckUnsafeNamesRejected()
    {
        foreach (string unsafeName in new[] { "../escape.png", "sub/dir.png", "C:\\abs.png", "", "  " })
        {
            try
            {
                ReviewBundleManifest.RequireSafeFileName(unsafeName);
                throw new InvalidOperationException($"Unsafe name accepted: '{unsafeName}'.");
            }
            catch (ArgumentException)
            {
            }
        }

        if (ReviewBundleManifest.RequireSafeFileName("review-raw.png") != "review-raw.png")
        {
            throw new InvalidOperationException("A safe name was altered.");
        }
    }

    private static void CheckSchemaRejected()
    {
        (ReviewBundleManifest.Manifest manifest, _, _) = SampleBundle();
        string json = ReviewBundleManifest.Serialize(manifest)
            .Replace(ReviewBundleManifest.Schema, "pd.other/v9", StringComparison.Ordinal);
        if (ReviewBundleManifest.TryParse(json, out _, out string? error) || string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException("A foreign-schema manifest was accepted.");
        }

        if (ReviewBundleManifest.TryParse("not json", out _, out error) || string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException("A non-JSON manifest was accepted.");
        }
    }

    private static string FreshDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pd-tools-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
