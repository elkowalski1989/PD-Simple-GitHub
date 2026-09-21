using PD.PcbTools.Review;

internal static class ReviewBundleChecks
{
    internal static void Run()
    {
        CheckRoundTrip();
        CheckVerifyDetectsTampering();
        CheckAtomicWriteFailureLeavesNoPartial();
        CheckPruneKeepsLinked();
        CheckUnsafeNamesRejected();
        CheckSchemaRejected();
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

    private static void CheckPruneKeepsLinked()
    {
        string directory = FreshDirectory();
        (ReviewBundleManifest.Manifest manifest, byte[] raw, _) = SampleBundle();
        File.WriteAllBytes(Path.Combine(directory, ReviewBundleManifest.ManifestFileName),
            "manifest"u8.ToArray());
        File.WriteAllBytes(Path.Combine(directory, "review-raw.png"), raw);
        File.WriteAllBytes(Path.Combine(directory, "review-annotated.png"), new byte[] { 9 });
        File.WriteAllBytes(Path.Combine(directory, "orphan-old.png"), new byte[] { 8 });
        IReadOnlyList<string> deleted = ReviewBundleManifest.PruneUnlinkedFiles(directory, manifest);
        if (deleted.Count != 1 || deleted[0] != "orphan-old.png")
        {
            throw new InvalidOperationException("Prune deleted a linked file or missed the orphan.");
        }

        if (!File.Exists(Path.Combine(directory, "review-raw.png")) ||
            !File.Exists(Path.Combine(directory, ReviewBundleManifest.ManifestFileName)))
        {
            throw new InvalidOperationException("Prune removed a manifest-linked file.");
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
