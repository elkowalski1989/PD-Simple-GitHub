using System.Text;
using System.Text.Json;
using System.IO;

namespace PD.Simple.Corridor;

public sealed record DpViaCorridorPoint(double XMil, double YMil);

public sealed record DpViaCorridorFinding(
    string Id, string PairName, string AggressorNet, string ObjectType, string Layer,
    string Category, string Risk, DpViaCorridorPoint P, DpViaCorridorPoint N,
    DpViaCorridorPoint? Intrusion, double DistanceMil, double HalfWidthMil, double HalfLengthMil);

/// <summary>Board-bound native results. All geometry is already in mils; SourceUnits is provenance only.</summary>
public sealed record DpViaCorridorResult(
    string Schema, string Status, long BoardGeneration, string Design, string Units, string SourceUnits,
    string ReportPath, string? NavigatorPath, bool NavigatorWritten,
    int PairCount, int CorridorCount, int AggressorCount, int FindingCount,
    int CriticalCount, int MediumCount, int LowCount, bool Truncated,
    IReadOnlyList<DpViaCorridorFinding> Findings)
{
    public IReadOnlyList<string> CoverageWarnings { get; init; } = Array.Empty<string>();
    public bool HasCompleteInputs => CoverageWarnings.Count == 0;

    public const string CurrentSchema = "pd-dp-via-corridor-result-v1";
    public const int MaximumPayloadBytes = 1024 * 1024;
    public const int MaximumFindings = 200;

    public static DpViaCorridorResult Parse(string json, long expectedBoardGeneration, string expectedDesign)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumPayloadBytes ||
            Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes)
        {
            throw Invalid("The result is empty or exceeds the 1 MiB payload limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            ValidateObject(root, "result", "schema", "status", "boardGeneration", "design", "units",
                "sourceUnits", "reportPath", "navigatorPath", "navigatorWritten", "pairCount", "corridorCount",
                "aggressorCount", "findingCount", "criticalCount", "mediumCount", "lowCount", "truncated", "findings");
            var schema = ReadString(root, "schema");
            if (schema != CurrentSchema)
            {
                throw Invalid("Unsupported result schema.");
            }

            var status = ReadString(root, "status");
            if (status != "complete")
            {
                throw Invalid("The native analysis did not complete.");
            }

            var generationValue = root.GetProperty("boardGeneration");
            if (generationValue.ValueKind != JsonValueKind.Number ||
                !generationValue.TryGetInt64(out var generation) || generation < 0)
            {
                throw Invalid("boardGeneration must be a nonnegative integer.");
            }

            if (generation != expectedBoardGeneration)
            {
                throw Invalid("The result belongs to a stale board generation.");
            }

            var design = ReadString(root, "design", 4096);
            if (!string.Equals(design, expectedDesign, StringComparison.Ordinal))
            {
                throw Invalid("The result belongs to a different design.");
            }

            var units = ReadString(root, "units");
            if (units != "mils")
            {
                throw Invalid("Result coordinates must use canonical mils.");
            }

            var sourceUnits = ReadString(root, "sourceUnits");
            if (sourceUnits is not ("mils" or "millimeters"))
            {
                throw Invalid("Unsupported sourceUnits.");
            }

            var reportPath = ReadString(root, "reportPath", 4096);
            var navigatorPath = root.GetProperty("navigatorPath").ValueKind == JsonValueKind.Null
                ? null : ReadString(root, "navigatorPath", 4096);
            var navigatorWritten = ReadBoolean(root, "navigatorWritten");
            if (navigatorWritten != (navigatorPath is not null))
            {
                throw Invalid("navigatorPath and navigatorWritten disagree.");
            }

            var pairCount = ReadCount(root, "pairCount");
            var corridorCount = ReadCount(root, "corridorCount");
            var aggressorCount = ReadCount(root, "aggressorCount");
            var findingCount = ReadCount(root, "findingCount");
            var criticalCount = ReadCount(root, "criticalCount");
            var mediumCount = ReadCount(root, "mediumCount");
            var lowCount = ReadCount(root, "lowCount");
            if ((long)criticalCount + mediumCount + lowCount != findingCount)
            {
                throw Invalid("Severity totals do not equal findingCount.");
            }

            var truncated = ReadBoolean(root, "truncated");
            var array = root.GetProperty("findings");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumFindings)
            {
                throw Invalid("findings must be an array containing at most 200 entries.");
            }

            if (array.GetArrayLength() > findingCount || truncated != (array.GetArrayLength() < findingCount))
            {
                throw Invalid("The returned finding count and truncation flag disagree with findingCount.");
            }

            var findings = new List<DpViaCorridorFinding>(array.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var criticalReturned = 0;
            var mediumReturned = 0;
            var lowReturned = 0;
            foreach (var item in array.EnumerateArray())
            {
                ValidateObject(item, "finding", "id", "pairName", "aggressorNet", "objectType", "layer",
                    "category", "risk", "p", "n", "intrusion", "distanceMil", "halfWidthMil", "halfLengthMil");
                var id = ReadString(item, "id", 128);
                if (!ids.Add(id))
                {
                    throw Invalid("Finding IDs must be unique.");
                }

                var risk = ReadString(item, "risk");
                switch (risk)
                {
                    case "CRITICAL":
                        criticalReturned++;
                        break;
                    case "MEDIUM":
                        mediumReturned++;
                        break;
                    case "LOW":
                        lowReturned++;
                        break;
                    default:
                        throw Invalid("Unsupported finding risk.");
                }
                findings.Add(new DpViaCorridorFinding(id,
                    ReadString(item, "pairName"), ReadString(item, "aggressorNet"),
                    ReadString(item, "objectType"), ReadString(item, "layer"), ReadString(item, "category"), risk,
                    ReadPoint(item.GetProperty("p"), "p"), ReadPoint(item.GetProperty("n"), "n"),
                    item.GetProperty("intrusion").ValueKind == JsonValueKind.Null
                        ? null : ReadPoint(item.GetProperty("intrusion"), "intrusion"),
                    ReadNumber(item, "distanceMil", true), ReadNumber(item, "halfWidthMil", true),
                    ReadNumber(item, "halfLengthMil", true)));
            }
            if (criticalReturned > criticalCount || mediumReturned > mediumCount || lowReturned > lowCount ||
                (!truncated && (criticalReturned != criticalCount || mediumReturned != mediumCount || lowReturned != lowCount)))
            {
                throw Invalid("Returned finding severities disagree with the severity totals.");
            }

            return new DpViaCorridorResult(schema, status, generation, design, units, sourceUnits,
                reportPath, navigatorPath, navigatorWritten, pairCount, corridorCount, aggressorCount,
                findingCount, criticalCount, mediumCount, lowCount, truncated, findings.AsReadOnly());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid DP via corridor result: malformed JSON.", exception);
        }
    }

    private static void ValidateObject(JsonElement element, string context, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{context} must be an object.");
        }

        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                throw Invalid($"{context} contains an unknown or duplicate field.");
            }
        }

        if (remaining.Count != 0)
        {
            throw Invalid($"{context} is missing required field '{remaining.First()}'.");
        }
    }

    private static string ReadString(JsonElement element, string name, int maximumLength = 1024)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{name} must be a string.");
        }

        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(char.IsControl))
        {
            throw Invalid($"{name} must be nonempty bounded text without control characters.");
        }

        return text;
    }

    private static bool ReadBoolean(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid($"{name} must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static int ReadCount(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
        {
            throw Invalid($"{name} must be a nonnegative integer.");
        }

        return count;
    }

    private static DpViaCorridorPoint ReadPoint(JsonElement element, string name)
    {
        ValidateObject(element, name, "xMil", "yMil");
        return new DpViaCorridorPoint(ReadNumber(element, "xMil"), ReadNumber(element, "yMil"));
    }

    private static double ReadNumber(JsonElement element, string name, bool nonnegative = false)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || (nonnegative && number < 0))
        {
            throw Invalid($"{name} must be a finite{(nonnegative ? " nonnegative" : "")} number.");
        }

        return number;
    }

    private static InvalidDataException Invalid(string message) => new($"Invalid DP via corridor result: {message}");
}
