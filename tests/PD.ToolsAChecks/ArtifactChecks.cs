using System.Collections.Immutable;
using PD.Simple.Tools.Analysis;

namespace PD.ToolsAChecks;

/// <summary>
/// Shared artifact and paging checks: atomic writes, overwrite refusal,
/// hash recording, staging cleanup, and bounded explicit pages.
/// </summary>
internal static class ArtifactChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;

        // Paging keeps totals visible and rejects bad requests.
        ImmutableArray<int> items = ImmutableArray.CreateRange(Enumerable.Range(0, 1234));
        LaneAPage<int> first = LaneAPaging.Page(items, 0, 500);
        LaneACheck.Require(
            first.Items.Length == 500 && first.PageCount == 3 && first.TotalCount == 1234,
            "First page is wrong.");
        LaneAPage<int> last = LaneAPaging.Page(items, 2, 500);
        LaneACheck.Require(last.Items.Length == 234, "Last page is wrong.");
        LaneAPage<int> beyond = LaneAPaging.Page(items, 9, 500);
        LaneACheck.Require(
            beyond.Items.Length == 0 && beyond.TotalCount == 1234,
            "An out-of-range page is not empty-with-totals.");
        LaneACheck.Require(
            LaneAPaging.Page(ImmutableArray<int>.Empty, 0).PageCount == 0,
            "An empty listing reports pages.");
        foreach (int bad in (int[])[0, -1, LaneAPaging.MaximumPageSize + 1])
        {
            bool rejected = false;
            try
            {
                LaneAPaging.Page(items, 0, bad);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }
            LaneACheck.Require(rejected, $"Page size {bad} was admitted.");
        }
        checks += 7;

        // Atomic writes refuse silent overwrites and record hashes.
        string directory = LaneACheck.NewTempDirectory("lane-a-artifacts-");
        try
        {
            string path = Path.Combine(directory, "sub", "output.json");
            (string written, string sha) = await LaneAArtifacts.WriteJsonAsync(
                path, new { tool = "T08", version = 1 }, false);
            LaneACheck.Require(File.Exists(written), "Atomic write left no file.");
            LaneACheck.Require(
                sha == LaneAArtifacts.ComputeFileSha256(written),
                "Recorded hash does not match the file.");
            bool refused = false;
            try
            {
                await LaneAArtifacts.WriteTextAsync(path, "overwrite", false);
            }
            catch (IOException)
            {
                refused = true;
            }
            LaneACheck.Require(refused, "Overwrite without approval succeeded.");
            (string rewritten, string resha) = await LaneAArtifacts.WriteTextAsync(path, "overwrite", true);
            LaneACheck.Require(
                rewritten == written && resha != sha &&
                await File.ReadAllTextAsync(rewritten) == "overwrite",
                "Approved overwrite did not replace the payload.");
            LaneACheck.Require(
                Directory.GetFiles(directory, "*.staging-*", SearchOption.AllDirectories).Length == 0,
                "Staging files leaked.");
            checks += 5;
        }
        finally
        {
            Directory.Delete(directory, true);
        }

        // Operation results keep their own identity and timings.
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var result = new LaneAOperationResult(
            Guid.NewGuid(), "T01", "Run", "Run", Guid.NewGuid(), "fixture",
            true, "ok", started, started + TimeSpan.FromSeconds(2),
            ImmutableArray<string>.Empty, null, null);
        LaneACheck.Require(result.Duration == TimeSpan.FromSeconds(2), "Result duration is wrong.");
        checks++;

        Console.WriteLine($"PASS: {checks} Lane A artifact and paging checks.");
        return checks;
    }
}
