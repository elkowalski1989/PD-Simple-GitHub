using System.Globalization;
using System.Text;

namespace PD.PcbTools;

/// <summary>
/// Renders the managed corridor screening report (.rpt) from one complete
/// <see cref="CorridorScan"/>. The text lists every acquired finding after
/// the totals header: search, filtering, paging, and selection all describe
/// this same complete set, so the exported totals always match what the user
/// can inspect. File placement stays with the caller. The scene GUID is the
/// planning catalog (A); <paramref name="scanCaptureIdentity"/> carries the
/// scan capture (B) identity text when the caller acquired one.
/// </summary>
public static class CorridorReportText
{
    public static string Build(CorridorScan scan, string design, string? scanCaptureIdentity = null)
    {
        var text = new StringBuilder();
        text.AppendLine("Differential Pair Via Corridor Screening Report");
        text.AppendLine($"Design: {design}");
        text.AppendLine($"Algorithm: {CorridorAnalyzer.Algorithm}");
        text.AppendLine(
            $"Input status: {(scan.HasCompleteInputs ? "Complete for reference screening" : "PARTIAL: no clear/pass conclusion is permitted")}");
        text.AppendLine(
            $"Native units: {scan.Scene.Document.NativeUnits}; report dimensions: mils");
        text.AppendLine(
            $"Scope: {scan.Options.ModuleName ?? "Whole board"}; " +
            $"Planning scene: {scan.Scene.Identity.CaptureId:N}; " +
            $"provider: {scan.Scene.Identity.Provenance.Provider}");
        if (!string.IsNullOrWhiteSpace(scanCaptureIdentity))
        {
            text.AppendLine($"Scan capture: {scanCaptureIdentity}");
        }
        string margin = scan.Options.MarginMils.ToString(
            "0.###",
            CultureInfo.InvariantCulture);
        text.AppendLine(
            $"Margin: {margin} mils; " +
            $"include unused pairs: {scan.Options.IncludeUnused}");
        text.AppendLine(
            $"Pairs: {scan.PairCount}; via corridors: {scan.CorridorCount}; " +
            $"findings: {scan.Findings.Count}");
        text.AppendLine(CorridorAnalyzer.Limitations);
        text.AppendLine(
            "Risk/category labels are retained rule classifications, " +
            "not measured coupling or capacitance.");
        foreach (string warning in scan.CoverageWarnings)
        {
            text.AppendLine("REVIEW REQUIRED: " + warning);
        }
        text.AppendLine();
        foreach (CorridorFinding finding in scan.Findings)
        {
            text.AppendLine(
                $"{finding.Id} | {finding.Risk} | {finding.Category} | " +
                $"{finding.PairName} | {finding.AggressorNet} | " +
                $"{finding.Layer} | {finding.ObjectType}");
            string pX = FormatCoordinate(finding.P.X);
            string pY = FormatCoordinate(finding.P.Y);
            string nX = FormatCoordinate(finding.N.X);
            string nY = FormatCoordinate(finding.N.Y);
            string distance = FormatCoordinate(finding.DistanceMils);
            string halfWidth = FormatCoordinate(finding.HalfWidthMils);
            text.AppendLine(
                $"  P=({pX}, {pY}); N=({nX}, {nY}); " +
                $"distance={distance}; half-width={halfWidth}");
        }
        return text.ToString();
    }

    private static string FormatCoordinate(decimal value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string FormatCoordinate(double value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);
}
