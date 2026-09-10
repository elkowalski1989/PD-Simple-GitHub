using System.IO;
using System.Security.Cryptography;
using System.Text;
using CircuitHub.AllegroBridge;

namespace PD.Simple;

internal static class SimpleToolExtension
{
    internal const string Id = "pd.simple.controls";
    private const string EntryFile = "pd_simple_controls.il";
    private static readonly Version Version = new(1, 0, 0);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Files =
    [
        EntryFile, "pd_simple_route_adapter.il", "49_route_interactive.il",
        "pd_dp_via_corridor.il", "dp_via_corridor_check.il", "categories.il"
    ];

    internal static AllegroSkillExtensionSource Source(string applicationDirectory) =>
        new(Id, Version, EntryFile,
            Files.Select(file => Path.Combine(applicationDirectory, "Skill", file)).ToArray())
        {
            DisplayName = "PD Simple tools"
        };

    internal static async Task<AllegroSkillExtensionIdentity> ReadIdentityAsync(
        string applicationDirectory, CancellationToken cancellationToken)
    {
        var files = new List<AllegroSkillFileIdentity>();
        foreach (string path in Source(applicationDirectory).Files)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            files.Add(new(Path.GetFileName(path), HashSource(bytes)));
        }
        string hash = AllegroSkillContentIdentity.Compute(Id, Version, EntryFile, files);
        return new(Id, Version, EntryFile, hash);
    }

    internal static string HashSource(byte[] bytes)
    {
        // Match the SDK's documented source identity: strip one UTF-8 BOM,
        // validate UTF-8, and preserve all other bytes, including line endings.
        ReadOnlySpan<byte> source = bytes;
        if (source.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            source = source[3..];
        }
        if (source.IsEmpty || source.Contains((byte)0))
        {
            throw new InvalidDataException("A PD Simple source file is empty or contains a NUL byte.");
        }
        _ = StrictUtf8.GetCharCount(source);
        return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
    }
}
