using System.IO;

namespace PD.Simple;

/// <summary>
/// Observes the resident bridge's supported focus signal without owning or
/// changing the bridge session. The UI polls this tiny file only while its
/// Engine session is alive.
/// </summary>
internal sealed class BridgeActivationSignal
{
    private readonly DateTime _notBeforeUtc;
    private string? _path;
    private DateTime _lastWriteUtc;

    internal BridgeActivationSignal(DateTime notBeforeUtc)
    {
        _notBeforeUtc = notBeforeUtc;
        _lastWriteUtc = notBeforeUtc;
    }

    internal static string? ResolveBridgeDirectory(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? candidate = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index] ?? string.Empty;
            if (argument.StartsWith("--bridge-dir=", StringComparison.OrdinalIgnoreCase))
            {
                candidate = argument["--bridge-dir=".Length..];
                break;
            }
            if (string.Equals(argument, "--bridge-dir", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count)
            {
                candidate = arguments[index + 1];
                break;
            }
        }

        candidate ??= Environment.GetEnvironmentVariable("PD_ALLEGRO_BRIDGE_DIR");
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(candidate);
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or
            NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void ObserveSession(string? bridgeDirectory)
    {
        string? nextPath = string.IsNullOrWhiteSpace(bridgeDirectory)
            ? null
            : Path.Combine(bridgeDirectory, "pd-simple-focus.signal");
        if (string.Equals(_path, nextPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _path = nextPath;
        _lastWriteUtc = _notBeforeUtc;
    }

    internal bool TryConsume()
    {
        if (_path is null || !File.Exists(_path))
        {
            return false;
        }

        DateTime writeUtc;
        try
        {
            writeUtc = File.GetLastWriteTimeUtc(_path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (writeUtc <= _lastWriteUtc)
        {
            return false;
        }

        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        _lastWriteUtc = writeUtc;
        return true;
    }
}
