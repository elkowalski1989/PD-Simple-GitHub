using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Manufacturing;
using PD.PcbTools.Manufacturing;

internal static class ManufacturingPageChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var model = new ManufacturingPageModel();
        check(!model.IsConnected &&
            model.SourceStatus.State == ManufacturingPageSourceState.Disconnected,
            "A fresh manufacturing page did not open disconnected.");
        check(!model.CanPlan(out string offlineReason) && offlineReason.Length != 0,
            "Planning was allowed without a source fence.");
        var (offlinePlan, offlineError) = model.TryBuildArtwork(
            [new("TOP", "ETCH/TOP", "films/top.gbr", false, false)],
            ArtworkDefaults(),
            "release",
            false);
        check(offlinePlan is null && offlineError is not null,
            "An Artwork plan was built while disconnected.");

        bool acquired = false;
        model.RefreshSource(() =>
        {
            acquired = true;
            throw new InvalidOperationException("No live board.");
        });
        check(!acquired && model.SourceStatus.State == ManufacturingPageSourceState.Disconnected,
            "Source refresh acquired while disconnected.");

        model.SetConnected(true);
        model.RefreshSource(() => throw new InvalidOperationException("The saved board source is unavailable."));
        check(model.SourceStatus.State == ManufacturingPageSourceState.NoCanonicalSource,
            "A missing canonical source was not reported.");
        model.RefreshSource(Source);
        check(model.SourceStatus.State == ManufacturingPageSourceState.Ready &&
            model.SourceStatus.Capture is not null &&
            model.CanPlan(out _),
            "A ready source fence was not accepted.");

        var (artwork, artworkError) = model.TryBuildArtwork(
            [
                new("TOP", "ETCH/TOP", "films/top.gbr", false, false),
                new("BOTTOM", "ETCH/BOTTOM", "films/bottom.gbr", true, true),
            ],
            ArtworkDefaults(),
            "release-page",
            false);
        check(artwork is not null && artworkError is null &&
            artwork.Films.Length == 2 &&
            artwork.Films[1].Polarity == ArtworkPolarity.Negative &&
            ManufacturingPageModel.PreviewArtworkManifest(artwork).Count == 3,
            "A valid Artwork plan or its manifest preview was wrong.");

        var (dupe, dupeError) = model.TryBuildArtwork(
            [
                new("TOP", "ETCH/TOP", "films/top.gbr", false, false),
                new("TOP", "ETCH/BOTTOM", "films/bottom.gbr", false, false),
            ],
            ArtworkDefaults(),
            "release-dupe",
            false);
        check(dupe is null && dupeError is not null,
            "Duplicate Artwork film names were planned.");

        var (noLayers, noLayersError) = model.TryBuildArtwork(
            [new("TOP", "   ", "films/top.gbr", false, false)],
            ArtworkDefaults(),
            "release-nolayers",
            false);
        check(noLayers is null && noLayersError is not null,
            "A filmless-layer Artwork plan was accepted.");

        var (odb, odbError) = model.TryBuildOdbPlusPlus(
            "primary",
            "ETCH/TOP, ETCH/BOTTOM",
            new(
                OdbPlusPlusOutputMode.Directory, null, null,
                OdbPlusPlusPadflashHandling.Default, OdbPlusPlusComponentOutlineSource.Default,
                null, null, false, false),
            "release-odb",
            false);
        check(odb is not null && odbError is null &&
            ManufacturingPageModel.PreviewOdbManifest(odb).Count == 3,
            "A valid ODB++ plan or its manifest preview was wrong.");
        var (odbEmpty, odbEmptyError) = model.TryBuildOdbPlusPlus(
            "primary", " , ", odb!.Options, "release-odb-empty", false);
        check(odbEmpty is null && odbEmptyError is not null,
            "An ODB++ plan without layers was accepted.");

        var (ipc, ipcError) = model.TryBuildIpc2581(
            "ETCH/TOP",
            "ipc/job.xml",
            new(Ipc2581Revision.C, Ipc2581Units.Millimeters, Ipc2581Content.LayerStackup),
            "release-ipc",
            false);
        check(ipc is not null && ipcError is null &&
            ManufacturingPageModel.PreviewIpcManifest(ipc).Single() == "ipc/job.xml",
            "A valid IPC-2581 plan or its manifest preview was wrong.");

        var context = new ManufacturingRunnerContext(Path.GetTempPath(), null, TimeSpan.FromMinutes(5));
        var runner = new UnqualifiedManufacturingRunner();
        StagedArtworkResult artworkRun = model.RunArtworkAsync(artwork!, context, runner).GetAwaiter().GetResult();
        check(artworkRun.Result.State == ManufacturingOutputState.NoGo &&
            artworkRun.Staging is null &&
            artworkRun.Result.Code == "artwork_native_contract_unqualified" &&
            ManufacturingPageModel.SummarizeArtwork(artworkRun.Result).Contains("NoGo"),
            "The default Artwork runner did not report honest NoGo.");
        StagedOdbPlusPlusResult odbRun = model.RunOdbPlusPlusAsync(odb, context, runner).GetAwaiter().GetResult();
        check(odbRun.Result.State == ManufacturingOutputState.NoGo &&
            ManufacturingPageModel.SummarizeOdb(odbRun.Result).Contains("NoGo"),
            "The default ODB++ runner did not report honest NoGo.");
        StagedIpc2581Result ipcRun = model.RunIpc2581Async(ipc!, context, runner).GetAwaiter().GetResult();
        check(ipcRun.Result.State == ManufacturingOutputState.NoGo &&
            ipcRun.Staging is null &&
            ManufacturingPageModel.SummarizeIpc(ipcRun.Result).Contains("NoGo"),
            "The default IPC-2581 runner did not report honest NoGo.");

        check(!model.CanPromote(artworkRun.Result.JobId, artworkRun.Result.State, artworkRun.Staging is not null, out string promoteReason) &&
            promoteReason.Length != 0,
            "Promotion was allowed for a NoGo result.");
        bool threw = false;
        try
        {
            model.Promote(artworkRun.Result.JobId, artworkRun.Result.State, null!);
        }
        catch (Exception exception) when (exception is ArgumentNullException or InvalidOperationException)
        {
            threw = true;
        }
        check(threw, "Promotion without staging did not refuse.");
        model.ReleaseStaging();

        EngineBinderChecks(model, artwork!, odb!, ipc!, check);
    }

    /// <summary>
    /// Release rebind: the Engine-backed exporter replaces the unqualified
    /// default at integration. Every path below runs offline with no vendor
    /// executable launched: presence probing, plan validation, and the live
    /// source fence all refuse before any launch.
    /// </summary>
    private static void EngineBinderChecks(
        ManufacturingPageModel model,
        ArtworkJobPlan artwork,
        OdbPlusPlusJobPlan odb,
        Ipc2581JobPlan ipc,
        Action<bool, string> check)
    {
        check(Throws<ArgumentNullException>(() => new EngineManufacturingExportRunner(null!)),
            "A null Engine workspace was accepted by the Engine-backed runner.");

        string? savedCdsRoot = Environment.GetEnvironmentVariable("CDSROOT");
        string? savedCdsRootAlt = Environment.GetEnvironmentVariable("CDS_ROOT");
        Environment.SetEnvironmentVariable("CDSROOT", null);
        Environment.SetEnvironmentVariable("CDS_ROOT", null);
        AllegroEngineSession binderSession = AllegroEngineSession.Create();
        try
        {
            var engineRunner = new EngineManufacturingExportRunner(binderSession.Workspace);
            var noRoot = new ManufacturingRunnerContext(
                Path.GetTempPath(), null, TimeSpan.FromMinutes(5), ArtParamText: "art_param");
            check(Throws<InvalidOperationException>(
                    () => model.RunArtworkAsync(artwork, noRoot, engineRunner).GetAwaiter().GetResult(),
                    "CDSROOT"),
                "Artwork without a Cadence root did not name the missing root.");

            StagedArtworkResult? before = model.LastArtwork;
            var bogusRoot = new ManufacturingRunnerContext(
                Path.GetTempPath(),
                Path.Combine(Path.GetTempPath(), "pd-no-such-cadence-root"),
                TimeSpan.FromMinutes(5), ArtParamText: "art_param");
            check(Throws<FileNotFoundException>(
                    () => model.RunArtworkAsync(artwork, bogusRoot, engineRunner).GetAwaiter().GetResult()),
                "Artwork with a missing native launcher did not refuse before staging.");
            check(ReferenceEquals(model.LastArtwork, before),
                "A refused Engine run replaced the previously staged result.");

            StagedOdbPlusPlusResult odbHeadless = model.RunOdbPlusPlusAsync(odb, noRoot, engineRunner)
                .GetAwaiter().GetResult();
            check(odbHeadless.Result.State == ManufacturingOutputState.NoGo &&
                ManufacturingPageModel.SummarizeOdb(odbHeadless.Result).Contains("NoGo"),
                "The Engine-backed ODB++ path did not report headless NoGo without launching.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            check(Throws<OperationCanceledException>(
                    () => model.RunOdbPlusPlusAsync(odb, noRoot, engineRunner, cancelled.Token).GetAwaiter().GetResult()),
                "A cancelled Engine run did not honor cancellation.");

            string fakeRoot = Path.Combine(Path.GetTempPath(), "pd-fake-cadence-root");
            string fakeBin = Path.Combine(fakeRoot, "tools", "bin");
            Directory.CreateDirectory(fakeBin);
            File.WriteAllText(Path.Combine(fakeBin, "artwork.exe"), "presence probe only; never launched");
            try
            {
                var noParam = new ManufacturingRunnerContext(
                    fakeRoot, fakeRoot, TimeSpan.FromMinutes(5));
                check(Throws<InvalidOperationException>(
                        () => model.RunArtworkAsync(artwork, noParam, engineRunner).GetAwaiter().GetResult(),
                        "art_param.txt"),
                    "Artwork without caller-supplied art_param.txt did not refuse before launching.");
                var withParam = noParam with { ArtParamText = "art_param" };
                check(Throws<InvalidOperationException>(
                        () => model.RunArtworkAsync(artwork, withParam, engineRunner).GetAwaiter().GetResult()),
                    "Artwork without a live canonical source did not refuse before launching.");
                check(Throws<InvalidOperationException>(
                        () => model.RunIpc2581Async(ipc, noRoot, engineRunner).GetAwaiter().GetResult(),
                        "CDSROOT"),
                    "IPC-2581 without a Cadence root did not name the missing root.");
            }
            finally
            {
                File.Delete(Path.Combine(fakeBin, "artwork.exe"));
                Directory.Delete(fakeBin);
                Directory.Delete(Path.Combine(fakeRoot, "tools"));
                Directory.Delete(fakeRoot);
            }

            check(Throws<ArgumentNullException>(
                    () => model.RunArtworkAsync(null!, noRoot, engineRunner).GetAwaiter().GetResult()),
                "A null Artwork plan was accepted by the Engine-backed runner.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDSROOT", savedCdsRoot);
            Environment.SetEnvironmentVariable("CDS_ROOT", savedCdsRootAlt);
            binderSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static bool Throws<TException>(Func<object?> action, string? mustContain = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return mustContain is null || error.Message.Contains(mustContain, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
        return false;
    }

    private static ManufacturingSourceCapture Source() =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new WorkspaceDocumentIdentity("session-page", 2, 3, 4242, "board-page.brd", "PD_V25"),
            7,
            new string('a', 64),
            new string('c', 64));

    private static ArtworkOptions ArtworkDefaults() =>
        new(
            ArtworkGerberFormat.Rs274X,
            ArtworkCoordinateUnits.Inches,
            SuppressNegativeFilmShapeArrayFill: false,
            UseVectorPadBehaviorForRasterArtwork: false);
}
