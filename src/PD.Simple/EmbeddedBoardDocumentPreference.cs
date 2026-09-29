using System.IO;
using System.Text.Json;

namespace PD.Simple;

internal sealed record PdSimplePreferences
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool EmbeddedBoardDocumentEnabled { get; init; }

    public string BackgroundCaptureProduct { get; init; } = string.Empty;
}

internal interface IPdSimplePreferenceStore
{
    PdSimplePreferences Load();

    void Save(PdSimplePreferences preferences);
}

/// <summary>
/// Owns user-facing PD Simple preferences. The background capture product is
/// an explicit launch token for an isolated read-only worker; changing it never
/// changes the primary Allegro session or the board's product tier.
/// </summary>
internal sealed class JsonPdSimplePreferenceStore : IPdSimplePreferenceStore
{
    internal const string PathVariable = "PD_SIMPLE_PREFERENCES_PATH";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    internal JsonPdSimplePreferenceStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A preference path is required.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    internal static JsonPdSimplePreferenceStore CreateDefault()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(PathVariable);
        if (configuredPath is null)
        {
            return new(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PD-Simple",
                "preferences.json"));
        }

        try
        {
            if (string.IsNullOrWhiteSpace(configuredPath) ||
                !Path.IsPathFullyQualified(configuredPath) ||
                string.IsNullOrEmpty(Path.GetFileName(configuredPath)) ||
                Path.GetFileName(configuredPath).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Directory.Exists(configuredPath))
            {
                throw new ArgumentException("The preference override must select an absolute file path.");
            }
            return new(configuredPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                $"Set {PathVariable} to an absolute preferences file path, or remove it to use the normal PD Simple preferences.",
                error);
        }
    }

    public PdSimplePreferences Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new();
            }

            PdSimplePreferences? preferences = JsonSerializer.Deserialize<PdSimplePreferences>(
                File.ReadAllText(_path),
                JsonOptions);
            return preferences?.SchemaVersion == PdSimplePreferences.CurrentSchemaVersion
                ? preferences
                : new();
        }
        catch (IOException)
        {
            return new();
        }
        catch (UnauthorizedAccessException)
        {
            return new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(PdSimplePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.SchemaVersion != PdSimplePreferences.CurrentSchemaVersion)
        {
            throw new ArgumentException(
                "Only the current PD Simple preference schema can be saved.",
                nameof(preferences));
        }

        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The preference path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
