using System.Collections.Immutable;
using System.Globalization;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Queries;

namespace IndependentAnalysisExtensionSample;

public enum DerivedReviewState
{
    InsideThreshold,
    BoundaryUncertain,
    NearOutside,
    OutsideReviewBand
}

/// <summary>Application-owned interpretation layered over Engine measurements.</summary>
public sealed record ProximityReviewOptions(
    string InterpretationId,
    Length OutsideReviewMargin)
{
    public bool IncludeOutsideReviewBand { get; init; }

    public int MaximumResults { get; init; } = 10_000;
}

public sealed record DerivedProximityResult(
    SceneObjectReference Subject,
    SceneObjectReference Target,
    ProximityLayerPair Layers,
    Length Distance,
    Length ErrorBound,
    DesignPoint SubjectWitness,
    DesignPoint TargetWitness,
    ProximityClassification EngineClassification,
    DerivedReviewState ReviewState,
    string InterpretationId);

public sealed record ProximityReview(
    Guid CaptureId,
    string InterpretationId,
    ImmutableArray<DerivedProximityResult> Results,
    bool ResultsTruncated,
    AnnotationScene Annotations);

/// <summary>
/// An independently owned interpretation extension. It consumes only public
/// Engine query output and creates new immutable results and annotations.
/// </summary>
public static class ProximityReviewExtension
{
    public static ProximityReview Create(
        ProximityAnalysis analysis,
        ProximityReviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        Validate(options);

        ImmutableArray<DerivedProximityResult> candidates = analysis.Results
            .Select(result => Derive(analysis, result, options))
            .Where(result => options.IncludeOutsideReviewBand ||
                result.ReviewState != DerivedReviewState.OutsideReviewBand)
            .ToImmutableArray();
        bool truncated = candidates.Length > options.MaximumResults;
        ImmutableArray<DerivedProximityResult> results = candidates
            .Take(options.MaximumResults)
            .ToImmutableArray();
        AnnotationScene annotations = AnnotationScene.Empty(analysis.CaptureId)
            .Replace(results.Select(AnnotationOf));
        return new(analysis.CaptureId, options.InterpretationId, results,
            truncated, annotations);
    }

    private static DerivedProximityResult Derive(
        ProximityAnalysis analysis,
        QualifiedProximityResult result,
        ProximityReviewOptions options)
    {
        decimal outsideBand = checked(
            analysis.Specification.Threshold.Mils + options.OutsideReviewMargin.Mils);
        decimal nearestPossibleDistance = Math.Max(0,
            result.Distance.Mils - result.ErrorBound.Mils);
        DerivedReviewState state = result.Classification switch
        {
            ProximityClassification.Within => DerivedReviewState.InsideThreshold,
            ProximityClassification.Indeterminate => DerivedReviewState.BoundaryUncertain,
            ProximityClassification.Outside when nearestPossibleDistance <= outsideBand =>
                DerivedReviewState.NearOutside,
            ProximityClassification.Outside => DerivedReviewState.OutsideReviewBand,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        return new(result.Subject, result.Target, result.Layers, result.Distance,
            result.ErrorBound, result.SubjectWitness, result.TargetWitness,
            result.Classification, state, options.InterpretationId);
    }

    private static Annotation AnnotationOf(DerivedProximityResult result)
    {
        GeometryShape geometry = result.SubjectWitness == result.TargetWitness
            ? new PointGeometry(result.SubjectWitness)
            : new LineGeometry(result.SubjectWitness, result.TargetWitness);
        AnnotationRole role = result.ReviewState switch
        {
            DerivedReviewState.InsideThreshold => AnnotationRole.Finding,
            DerivedReviewState.BoundaryUncertain => AnnotationRole.Warning,
            DerivedReviewState.NearOutside => AnnotationRole.Guide,
            DerivedReviewState.OutsideReviewBand => AnnotationRole.Information,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        string label = string.Create(CultureInfo.InvariantCulture,
            $"{result.InterpretationId}: {result.ReviewState}, {result.Distance.Mils:0.########} mil ± {result.ErrorBound.Mils:0.########} mil");
        string id = $"{result.InterpretationId}:{result.Subject.ObjectId.Value}:{result.Target.ObjectId.Value}:" +
            $"{result.Layers.SubjectLayer.Value}:{result.Layers.TargetLayer.Value}";
        if (id.Length > 256)
        {
            id = $"{result.InterpretationId}:{StableId(id)}";
        }
        return new(id, geometry, role, label, Source: result.Subject);
    }

    private static string StableId(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        ulong hash = offset;
        foreach (char character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static void Validate(ProximityReviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.InterpretationId) ||
            options.InterpretationId.Length > 128 ||
            options.InterpretationId.Any(char.IsControl) ||
            options.OutsideReviewMargin.Mils < 0 ||
            options.MaximumResults is < 1 or > 10_000)
        {
            throw new ArgumentException(
                "Derived review options require a bounded ID, nonnegative margin, and result budget.",
                nameof(options));
        }
    }
}
