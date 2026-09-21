using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.ToolsAChecks;

/// <summary>
/// Shared offline helpers: assertions, repository root lookup, temp
/// directories, and deterministic synthetic scene fixtures built only from
/// public Engine constructors. Fixtures use <see cref="CaptureAcquisitionKind.Synthetic"/>
/// provenance and are labeled as such; they never close native gates.
/// </summary>
internal static class LaneACheck
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Lane A check failed: " + message);
        }
    }

    public static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.targets")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "PD.Simple")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate the PD Simple repository root.");
    }

    public static string NewTempDirectory(string prefix)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static DesignPoint Mil(decimal x, decimal y) =>
        DesignPoint.From(x, y, LengthUnit.Mils);

    public static LineGeometry MilLine(decimal x1, decimal y1, decimal x2, decimal y2) =>
        new(Mil(x1, y1), Mil(x2, y2));

    public static FamilyAcquisition FixtureAcquisition(string family) =>
        new(
            family,
            "native-token-" + family,
            "parent-token-lane-a",
            "lane-a-fixture",
            1L,
            DateTimeOffset.UtcNow,
            "synthetic deterministic fixture");

    public static FamilyCoverage CompleteCoverage(DataFamily family, int observed) =>
        new(
            family,
            DataAvailability.Available,
            DataCompleteness.CompleteForRequestedScope,
            GeometryFidelity.AnalyticPrimitive,
            ImmutableArray<string>.Empty,
            FixtureAcquisition(family.ToString()),
            false,
            observed,
            observed);

    /// <summary>
    /// Copper scope covering every kind. The fixture models a small board
    /// whose via/shape/pin kinds were acquired and found empty; unknown
    /// kinds could never be reported complete by the analyzer.
    /// </summary>
    public static CopperReadScope CompleteCopperScope() =>
        new(
            ImmutableArray.Create(
                CopperKind.Trace, CopperKind.Via, CopperKind.Shape, CopperKind.Pin),
            ImmutableArray.Create(
                CompleteKindCoverage(CopperKind.Trace),
                CompleteKindCoverage(CopperKind.Via),
                CompleteKindCoverage(CopperKind.Shape),
                CompleteKindCoverage(CopperKind.Pin)));

    private static CopperKindCoverage CompleteKindCoverage(CopperKind kind) =>
        new(
            kind,
            DataAvailability.Available,
            DataCompleteness.CompleteForRequestedScope,
            GeometryFidelity.AnalyticPrimitive,
            ImmutableArray<string>.Empty);

    public static FamilyCoverage MissingCoverage(DataFamily family, string reason) =>
        new(
            family,
            DataAvailability.Unavailable,
            DataCompleteness.Partial,
            GeometryFidelity.Unknown,
            ImmutableArray.Create(reason),
            FixtureAcquisition(family.ToString()),
            false,
            null,
            null);

    /// <summary>
    /// Copper was acquired but is incomplete (bounds only, truncated): data
    /// is present yet qualified-geometry requirements must fail.
    /// </summary>
    public static FamilyCoverage PartialCopperCoverage() =>
        new(
            DataFamily.Copper,
            DataAvailability.Available,
            DataCompleteness.Partial,
            GeometryFidelity.BoundsOnly,
            ImmutableArray.Create("Only bounds were acquired; qualified contours are missing."),
            FixtureAcquisition("Copper"),
            true,
            4,
            12);

    public static CopperObject FixtureTrace(
        string id, string net, string layer, LineGeometry centerline, decimal widthMils)
    {
        var layerId = new LayerId(layer);
        // Match the Engine example's valid trace evidence: width-expanded
        // bounds, no surfaces, and absent via/pin evidence (traces carry
        // neither; Engine rejects empty-but-present records).
        DesignBounds bounds = DesignBounds.FromPoints(
                new[] { centerline.Start, centerline.End })
            .Inflate(Length.From(widthMils / 2m, LengthUnit.Mils));
        return new CopperObject(
            new SceneObjectId(id),
            CopperKind.Trace,
            net,
            layerId,
            bounds,
            centerline,
            Length.From(widthMils, LengthUnit.Mils),
            null!,
            ImmutableArray<CopperSurface>.Empty,
            null,
            ImmutableArray<string>.Empty,
            null!,
            new CopperGeometryModel(
                ImmutableArray<LayerGeometryRecord>.Empty,
                ImmutableArray<string>.Empty));
    }

    /// <summary>
    /// Two crossing diagonals on ETCH/TOP (nets N1/N2, meet at 50,50), one
    /// parallel line on ETCH/BOT, and a collinear duplicate of the rising
    /// diagonal on ETCH/TOP under another net. Complete copper coverage.
    /// </summary>
    public static DesignScene CrossingFixture()
    {
        var top = new LayerId("ETCH/TOP");
        var bottom = new LayerId("ETCH/BOT");
        var rising = MilLine(0m, 0m, 100m, 100m);
        var falling = MilLine(0m, 100m, 100m, 0m);
        var parallel = MilLine(0m, 120m, 100m, 120m);
        var duplicate = MilLine(0m, 0m, 100m, 100m);
        var data = new SceneData
        {
            CopperScope = CompleteCopperScope(),
            Copper = ImmutableArray.Create(
                FixtureTrace("trace-rising", "N1", top.Value, rising, 5m),
                FixtureTrace("trace-falling", "N2", top.Value, falling, 5m),
                FixtureTrace("trace-parallel", "N3", bottom.Value, parallel, 5m),
                FixtureTrace("trace-duplicate", "N4", top.Value, duplicate, 5m)),
            Nets = ImmutableArray.Create(
                new NetObject(new SceneObjectId("net-n1"), "N1", 0),
                new NetObject(new SceneObjectId("net-n2"), "N2", 0),
                new NetObject(new SceneObjectId("net-n3"), "N3", 0),
                new NetObject(new SceneObjectId("net-n4"), "N4", 0)),
            Layers = ImmutableArray.Create(
                new LayerObject(top, 0, false, true, true),
                new LayerObject(bottom, 1, false, true, false)),
        };
        return new DesignScene(
            new SceneIdentity(
                Guid.NewGuid(), DateTimeOffset.UtcNow,
                new CaptureProvenance(
                    "lane-a-fixture", "1", true, "synthetic", CaptureAcquisitionKind.Synthetic)),
            new DocumentContext(
                DocumentKind.PcbBoard, "lane-a-crossing.brd", "mils", 2,
                new DesignBounds(Mil(-10m, -10m), Mil(200m, 200m)),
                "saved-fixture-1", "open-fixture-1"),
            new SceneQuery
            {
                Kind = SceneReadKind.CompleteBoard,
                Families = ImmutableArray.Create(DataFamily.Copper, DataFamily.Nets),
                MaximumObjects = 100,
            },
            new CoverageReport(new[]
            {
                CompleteCoverage(DataFamily.Copper, 4),
                CompleteCoverage(DataFamily.Nets, 4),
                CompleteCoverage(DataFamily.Layers, 2),
            }),
            data);
    }

    /// <summary>
    /// Same copper as <see cref="CrossingFixture"/> but copper coverage is
    /// unavailable, so strict gates must report missing scope.
    /// </summary>
    public static DesignScene PartialFixture()
    {
        DesignScene full = CrossingFixture();
        return new DesignScene(
            full.Identity,
            full.Document,
            full.Query,
            new CoverageReport(new[]
            {
                PartialCopperCoverage(),
                CompleteCoverage(DataFamily.Nets, 4),
                CompleteCoverage(DataFamily.Layers, 2),
            }),
            full.Data);
    }
}
