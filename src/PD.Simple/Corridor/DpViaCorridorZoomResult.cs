using System.IO;
using System.Text;
using System.Text.Json;

namespace PD.Simple.Corridor;

public sealed record DpViaCorridorBounds(
    double MinimumXMil, double MinimumYMil, double MaximumXMil, double MaximumYMil);

/// <summary>A board-bound native viewport readback, not a new analysis or historical viewport.</summary>
public sealed record DpViaCorridorZoomResult(
    string Schema, string Status, long BoardGeneration, string Design, string ReportPath,
    string FindingId, string Layer, string Units,
    DpViaCorridorBounds RequestedBounds, DpViaCorridorBounds ActualBounds)
{
    public const string CurrentSchema = "pd-dp-via-corridor-zoom-v1";
    public const int MaximumPayloadBytes = 64 * 1024;

    public static DpViaCorridorZoomResult Parse(string json, long expectedBoardGeneration,
        string expectedDesign, string expectedReportPath, string expectedFindingId, string expectedLayer)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumPayloadBytes ||
            Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes)
        {
            throw Invalid("The reply is empty or exceeds the 64 KiB payload limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            ValidateObject(root, "reply", "schema", "status", "boardGeneration", "design", "reportPath",
                "findingId", "layer", "units", "requestedBounds", "actualBounds");
            var schema = ReadString(root, "schema");
            if (schema != CurrentSchema)
            {
                throw Invalid("Unsupported zoom reply schema.");
            }

            var status = ReadString(root, "status");
            if (status != "complete")
            {
                throw Invalid("The native zoom did not complete.");
            }

            var generationValue = root.GetProperty("boardGeneration");
            if (generationValue.ValueKind != JsonValueKind.Number ||
                !generationValue.TryGetInt64(out var generation) || generation < 0)
            {
                throw Invalid("boardGeneration must be a nonnegative integer.");
            }

            if (generation != expectedBoardGeneration)
            {
                throw Invalid("The reply belongs to a stale board generation.");
            }

            var design = ReadExpectedString(root, "design", expectedDesign, 4096);
            var reportPath = ReadExpectedString(root, "reportPath", expectedReportPath, 4096);
            var findingId = ReadExpectedString(root, "findingId", expectedFindingId, 128);
            var layer = ReadExpectedString(root, "layer", expectedLayer);
            var units = ReadString(root, "units");
            if (units != "mils")
            {
                throw Invalid("Viewport bounds must use canonical mils.");
            }

            var requested = ReadBounds(root.GetProperty("requestedBounds"), "requestedBounds");
            var actual = ReadBounds(root.GetProperty("actualBounds"), "actualBounds");
            // The native viewport may expand to fit its aspect ratio, but must contain the
            // entire requested box. No geometry tolerance or guessed coordinates are applied.
            if (actual.MinimumXMil > requested.MinimumXMil || actual.MinimumYMil > requested.MinimumYMil ||
                actual.MaximumXMil < requested.MaximumXMil || actual.MaximumYMil < requested.MaximumYMil)
            {
                throw Invalid("The actual viewport does not contain the requested bounds.");
            }

            return new DpViaCorridorZoomResult(schema, status, generation, design, reportPath,
                findingId, layer, units, requested, actual);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid DP via corridor zoom reply: malformed JSON.", exception);
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

    private static string ReadExpectedString(JsonElement element, string name, string expected, int maximumLength = 1024)
    {
        var value = ReadString(element, name, maximumLength);
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw Invalid($"The reply {name} does not match the requested finding context.");
        }

        return value;
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

    private static DpViaCorridorBounds ReadBounds(JsonElement element, string name)
    {
        ValidateObject(element, name, "minimumXMil", "minimumYMil", "maximumXMil", "maximumYMil");
        var bounds = new DpViaCorridorBounds(ReadNumber(element, "minimumXMil"), ReadNumber(element, "minimumYMil"),
            ReadNumber(element, "maximumXMil"), ReadNumber(element, "maximumYMil"));
        if (bounds.MinimumXMil >= bounds.MaximumXMil || bounds.MinimumYMil >= bounds.MaximumYMil)
        {
            throw Invalid($"{name} must have ordered bounds with nonzero width and height.");
        }

        return bounds;
    }

    private static double ReadNumber(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            throw Invalid($"{name} must be a finite number.");
        }

        return number;
    }

    private static InvalidDataException Invalid(string message) => new($"Invalid DP via corridor zoom reply: {message}");
}
