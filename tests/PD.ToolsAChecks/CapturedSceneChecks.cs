using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple.Tools.Analysis;
using PD.Simple.Tools.Captures;

namespace PD.ToolsAChecks;

/// <summary>
/// T08 offline checks: explicit scope, archive save/open round-trips,
/// corrupt-input rejection, offline queries, manifests, close/release, and
/// reacquisition identity. Live bulk capture stays NOT_EXECUTED.
/// </summary>
internal static class CapturedSceneChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        checks += CheckExplicitScope();
        checks += await CheckArchiveRoundTripAsync();
        checks += await CheckCorruptArchivesAsync();
        checks += await CheckOfflineQueriesAsync();
        checks += await CheckManifestCloseAndReacquireAsync();
        Console.WriteLine($"PASS: {checks} T08 captured scene checks.");
        return checks;
    }

    private static int CheckExplicitScope()
    {
        var workspace = new CapturedSceneWorkspace();
        LaneACheck.Require(!workspace.CanCapture, "Capture is available with no live delegate.");
        LaneACheck.Require(
            workspace.CaptureBlockedReason.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
            "The capture block does not say live capture is unavailable.");

        // Contours stay off unless the caller opts in.
        SceneQuery query = workspace.BuildSceneQuery();
        LaneACheck.Require(!query.IncludeContours, "Contour detail is on without opt-in.");
        LaneACheck.Require(
            query.Families.Contains(DataFamily.Copper),
            "Default scope lost the Copper family.");
        LaneACheck.Require(
            query.MaximumObjects == CapturedSceneWorkspace.DefaultMaximumObjects,
            "Default object budget changed.");
        LaneACheck.Require(!query.Region.HasValue, "A region exists before entry.");

        // Region entry, layers, and clamping are explicit.
        workspace.RegionX1Mils = 0m;
        workspace.RegionY1Mils = 0m;
        workspace.RegionX2Mils = 500m;
        workspace.RegionY2Mils = 400m;
        workspace.LayerNamesText = "ETCH/TOP, ETCH/BOT";
        workspace.MaximumObjects = int.MaxValue;
        SceneQuery scoped = workspace.BuildSceneQuery();
        LaneACheck.Require(scoped.Region.HasValue, "Entered region was not kept.");
        LaneACheck.Require(
            scoped.Region!.Value.Width == 500m && scoped.Region.Value.Height == 400m,
            "Region extents moved.");
        LaneACheck.Require(scoped.Layers.Length == 2, "Layer scope was not kept.");
        LaneACheck.Require(
            scoped.MaximumObjects == CapturedSceneWorkspace.MaximumMaximumObjects,
            "Object budget was not clamped to its bound.");
        workspace.ClearRegion();
        LaneACheck.Require(!workspace.HasRegion, "Region was not cleared.");

        // Empty families fall back to Copper rather than an empty success.
        workspace.Families = ImmutableArray<DataFamily>.Empty;
        SceneQuery fallback = workspace.BuildSceneQuery();
        LaneACheck.Require(
            fallback.Families.Contains(DataFamily.Copper),
            "Empty families did not fall back to Copper.");
        return 10;
    }

    private static async Task<int> CheckArchiveRoundTripAsync()
    {
        string directory = LaneACheck.NewTempDirectory("lane-a-t08-");
        try
        {
            DesignScene fixture = LaneACheck.CrossingFixture();
            var workspace = new CapturedSceneWorkspace();
            workspace.AttachOfflineScene(fixture, "fixture");
            LaneACheck.Require(!workspace.LiveAuthority, "A fixture claims live authority.");
            LaneACheck.Require(
                workspace.LiveAuthorityText.Contains("none", StringComparison.OrdinalIgnoreCase),
                "Live-authority text does not deny authority for fixtures.");

            // Save then reopen the same pathname: the archive keeps its
            // capture identity, and loading grants no live authority.
            string archivePath = Path.Combine(directory, "scene.pdscene");
            LaneAOperationResult saved = await workspace.SaveArchiveAsync(archivePath, false);
            LaneACheck.Require(saved.IsSuccess, "Archive save failed: " + saved.Outcome);
            LaneACheck.Require(
                saved.ArtifactSha256 == LaneAArtifacts.ComputeFileSha256(archivePath),
                "Archive hash does not match the file.");
            LaneAOperationResult refusal = await workspace.SaveArchiveAsync(archivePath, false);
            LaneACheck.Require(!refusal.IsSuccess, "Archive save overwrote without approval.");

            var reopened = new CapturedSceneWorkspace();
            LaneAOperationResult opened = await reopened.OpenArchiveAsync(archivePath);
            LaneACheck.Require(opened.IsSuccess, "Archive open failed: " + opened.Outcome);
            LaneACheck.Require(
                reopened.SourceCaptureId == fixture.Identity.CaptureId,
                "Reopened archive lost its capture identity.");
            LaneACheck.Require(!reopened.LiveAuthority, "A reopened archive claims live authority.");
            LaneACheck.Require(
                reopened.CoverageSummary.Contains("complete", StringComparison.OrdinalIgnoreCase),
                "Reopened coverage is not explicit: " + reopened.CoverageSummary);

            // Stream round-trip preserves identity without touching disk.
            using var stream = new MemoryStream();
            await SceneArchive.WriteAsync(stream, fixture, CancellationToken.None);
            stream.Position = 0;
            DesignScene streamed = await SceneArchive.ReadAsync(stream, CancellationToken.None);
            LaneACheck.Require(
                streamed.Identity.CaptureId == fixture.Identity.CaptureId,
                "Stream round-trip lost the capture identity.");
            LaneACheck.Require(
                streamed.Copper is not null && fixture.Copper is not null &&
                streamed.Copper.Items.Length == fixture.Copper.Items.Length,
                "Stream round-trip lost copper records.");

            // Schema versions stay ordered: oldest readable never exceeds current.
            LaneACheck.Require(
                SceneArchiveIsOrdered(),
                "Archive schema versions are not ordered.");
            return 11;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static bool SceneArchiveIsOrdered() =>
        SceneArchive.OldestReadableSchemaVersion <= SceneArchive.SchemaVersion &&
        SceneArchive.MaximumDataBytes > 0;

    private static async Task<int> CheckCorruptArchivesAsync()
    {
        string directory = LaneACheck.NewTempDirectory("lane-a-t08-bad-");
        try
        {
            // Random bytes are rejected without a scene.
            string garbage = Path.Combine(directory, "garbage.pdscene");
            await File.WriteAllBytesAsync(garbage, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var workspace = new CapturedSceneWorkspace();
            LaneAOperationResult bad = await workspace.OpenArchiveAsync(garbage);
            LaneACheck.Require(!bad.IsSuccess, "Garbage bytes opened as a scene.");
            LaneACheck.Require(!workspace.HasScene, "Garbage bytes attached a scene.");

            // Truncated archives are rejected without a partial scene.
            DesignScene fixture = LaneACheck.CrossingFixture();
            string full = Path.Combine(directory, "full.pdscene");
            using (var stream = new MemoryStream())
            {
                await SceneArchive.WriteAsync(stream, fixture, CancellationToken.None);
                byte[] payload = stream.ToArray();
                await File.WriteAllBytesAsync(full, payload[..Math.Min(16, payload.Length)]);
            }
            LaneAOperationResult truncated = await workspace.OpenArchiveAsync(full);
            LaneACheck.Require(!truncated.IsSuccess, "A truncated archive opened.");
            LaneACheck.Require(!workspace.HasScene, "A truncated archive attached a scene.");

            // Bulk open on a non-capture directory is rejected offline.
            LaneAOperationResult bulk = await workspace.OpenBulkDirectoryAsync(directory);
            LaneACheck.Require(!bulk.IsSuccess, "Bulk open succeeded on a plain directory.");

            // Replay and export with no held bulk capture are rejected.
            LaneAOperationResult replay = await workspace.ReplayBulkAsync();
            LaneACheck.Require(!replay.IsSuccess, "Bulk replay succeeded with no capture.");
            LaneAOperationResult export = await workspace.ExportBulkAsync(Path.Combine(directory, "out"));
            LaneACheck.Require(!export.IsSuccess, "Bulk export succeeded with no capture.");
            return 7;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task<int> CheckOfflineQueriesAsync()
    {
        string directory = LaneACheck.NewTempDirectory("lane-a-t08-q-");
        try
        {
            DesignScene fixture = LaneACheck.CrossingFixture();
            string archivePath = Path.Combine(directory, "q.pdscene");
            var opener = new CapturedSceneWorkspace();
            opener.AttachOfflineScene(fixture, "fixture");
            LaneAOperationResult saved = await opener.SaveArchiveAsync(archivePath, false);
            LaneACheck.Require(saved.IsSuccess, "Query fixture save failed.");

            var workspace = new CapturedSceneWorkspace();
            LaneAOperationResult opened = await workspace.OpenArchiveAsync(archivePath);
            LaneACheck.Require(opened.IsSuccess, "Query fixture open failed.");

            // OfflineDesignSource answers explicit queries with no native work.
            var source = new OfflineDesignSource(workspace.HasScene
                ? await SceneArchive.LoadAsync(archivePath, CancellationToken.None)
                : throw new InvalidOperationException("No scene for offline query."));
            SceneRead read = await source.AcquireAsync(
                new SceneQuery
                {
                    Kind = SceneReadKind.CompleteBoard,
                    Families = ImmutableArray.Create(DataFamily.Copper),
                    MaximumObjects = 10,
                },
                CancellationToken.None);
            LaneACheck.Require(
                read.Scene is not null &&
                read.Scene.Identity.CaptureId == fixture.Identity.CaptureId,
                "Offline query answered from the wrong capture.");

            // Workspace offline query runs caller computation authority-free.
            LaneAOperationResult queried = await workspace.RunOfflineQueryAsync(
                (scene, token) =>
                {
                    var browser = new SceneObjectBrowser(scene);
                    ObjectSearchResult hits = browser.Search(
                        ObjectFamily.Traces, string.Empty, 100, token);
                    return Task.FromResult($"Offline trace rows: {hits.Items.Length}.");
                },
                "trace-rows");
            LaneACheck.Require(queried.IsSuccess, "Offline query failed: " + queried.Outcome);
            LaneACheck.Require(
                queried.Outcome.Contains("Offline trace rows:", StringComparison.Ordinal),
                "Offline query outcome is not explicit.");

            // Offline query with no scene is rejected, never empty success.
            var empty = new CapturedSceneWorkspace();
            LaneAOperationResult none = await empty.RunOfflineQueryAsync(
                (_, _) => Task.FromResult("unreachable"), "trace-rows");
            LaneACheck.Require(!none.IsSuccess, "Offline query succeeded with no scene.");
            return 6;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task<int> CheckManifestCloseAndReacquireAsync()
    {
        string directory = LaneACheck.NewTempDirectory("lane-a-t08-m-");
        try
        {
            DesignScene first = LaneACheck.CrossingFixture();
            DesignScene second = LaneACheck.CrossingFixture();
            LaneACheck.Require(
                first.Identity.CaptureId != second.Identity.CaptureId,
                "Fixture captures are not distinct.");

            // Stub live acquisition: each call issues a fresh capture.
            var offered = new Queue<DesignScene>(new[] { first, second });
            Task<DesignScene> Acquire(SceneQuery query, IProgress<EngineDiagnostic>? progress, CancellationToken token)
            {
                LaneACheck.Require(
                    query.Families.Contains(DataFamily.Copper),
                    "Live acquisition lost the Copper family.");
                return Task.FromResult(offered.Dequeue());
            }
            var workspace = new CapturedSceneWorkspace(Acquire, null);

            LaneAOperationResult captured = await workspace.CaptureNowAsync();
            LaneACheck.Require(captured.IsSuccess, "Stub capture failed: " + captured.Outcome);
            LaneACheck.Require(workspace.LiveAuthority, "A live capture has no live authority.");
            LaneACheck.Require(
                workspace.SourceCaptureId == first.Identity.CaptureId,
                "Capture did not attach the fresh scene.");

            string manifestPath = Path.Combine(directory, "manifest.json");
            LaneAOperationResult manifest = await workspace.ExportManifestAsync(manifestPath, false);
            LaneACheck.Require(manifest.IsSuccess, "Manifest export failed: " + manifest.Outcome);
            string manifestText = await File.ReadAllTextAsync(manifestPath);
            LaneACheck.Require(
                manifestText.Contains(first.Identity.CaptureId.ToString(), StringComparison.Ordinal),
                "The manifest does not carry the capture identity.");
            LaneACheck.Require(
                manifestText.Contains("LiveAuthority", StringComparison.OrdinalIgnoreCase),
                "The manifest does not record live authority.");

            // Reacquire issues a new request with a new identity.
            LaneAOperationResult reacquired = await workspace.ReacquireAsync();
            LaneACheck.Require(reacquired.IsSuccess, "Reacquisition failed: " + reacquired.Outcome);
            LaneACheck.Require(
                workspace.SourceCaptureId == second.Identity.CaptureId,
                "Reacquisition did not attach the new capture.");

            // Close releases the input but preserves exported files.
            LaneAOperationResult closed = await workspace.CloseAsync();
            LaneACheck.Require(closed.IsSuccess, "Close failed.");
            LaneACheck.Require(!workspace.HasScene, "Close kept the scene attached.");
            LaneACheck.Require(!workspace.LiveAuthority, "Close kept live authority.");
            LaneACheck.Require(
                File.Exists(manifestPath),
                "Close removed the exported manifest.");
            await workspace.DisposeAsync();
            return 12;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
