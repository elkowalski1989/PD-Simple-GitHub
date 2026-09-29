using System.Security.Cryptography;
using System.ComponentModel;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Reviews;

namespace PD.PcbTools;

public sealed record PhysicalSymbolStageCopy(
    EngineSymbolWorkArea Area, string SourcePath, long Bytes, string SourceSha256, string StagedSha256)
{
    public const long MaximumDraBytes = 256L * 1024 * 1024;
    public ImmutableArray<string> CleanupWarnings { get; init; } = [];

    /// <summary>Copies only the explicitly selected DRA; native PACKAGE identity is inspected separately.</summary>
    public static async ValueTask<PhysicalSymbolStageCopy> CreateAsync(string sourcePath, string stagingRoot,
        string symbolName, string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath) ||
            !string.Equals(Path.GetExtension(sourcePath), ".dra", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select an absolute path to an existing DRA file.", nameof(sourcePath));
        }
        if (!Path.IsPathFullyQualified(stagingRoot) || !Directory.Exists(stagingRoot))
        {
            throw new ArgumentException("Select an existing absolute parent directory for the new owned stage.", nameof(stagingRoot));
        }
        string source = Path.GetFullPath(sourcePath);
        string destinationDirectory = Path.Combine(Path.GetFullPath(stagingRoot), "symbol-stage-" + Guid.NewGuid().ToString("N"));
        EngineSymbolWorkArea area = EnginePhysicalSymbolStagingWorkflow.PlanStage(destinationDirectory, symbolName, owner);
        ArchiveFile.RequireNonRedirectedLocalPath(source);
        ArchiveFile.RequireNonRedirectedLocalPath(area.StagedDraPath);
        long expectedBytes;
        using (var selected = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            expectedBytes = selected.Length;
            if (expectedBytes is < 1 or > MaximumDraBytes)
            {
                throw new InvalidDataException("The selected DRA is empty or exceeds the 256 MiB staging bound.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        CreateNewDirectory(destinationDirectory);
        string sourceHash = string.Empty;
        ArchiveSaveResult saved;
        try
        {
            saved = await ArchiveFile.SaveNewAsync(area.StagedDraPath, async (output, token) =>
            {
                // All source handles and verification finish before the shared owner's atomic commit.
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != expectedBytes)
                {
                    throw new InvalidDataException("The selected source DRA changed before staging began.");
                }
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[64 * 1024];
                long copied = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    copied = checked(copied + count);
                    if (copied > expectedBytes || copied > MaximumDraBytes)
                    {
                        throw new InvalidDataException("The source DRA changed or exceeded the staging bound during copy.");
                    }
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (copied != expectedBytes)
                {
                    throw new InvalidDataException("The source DRA length changed during copy.");
                }
                string copiedHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                sourceHash = await HashAsync(source, expectedBytes, token).ConfigureAwait(false);
                output.Position = 0;
                string temporaryHash = Convert.ToHexString(await SHA256.HashDataAsync(output, token)
                    .ConfigureAwait(false)).ToLowerInvariant();
                if (output.Length != expectedBytes || copiedHash != sourceHash || copiedHash != temporaryHash)
                {
                    throw new InvalidDataException("Source and staged DRA bytes no longer agree before commit.");
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException error)
        {
            // No directory deletion by path: its identity may have been replaced after creation.
            throw new OperationCanceledException(
                $"Staging was canceled before commit. The stage directory is retained at {destinationDirectory}.",
                error, cancellationToken);
        }
        catch (Exception error)
        {
            throw new IOException(
                $"Staging did not commit a DRA. The stage directory is retained at {destinationDirectory}. " +
                error.Message, error);
        }
        // The shared owner has committed. Do not consult cancellation or reopen/delete the published path.
        return new(area, source, saved.ByteLength, sourceHash, saved.Sha256)
        {
            CleanupWarnings = saved.CleanupWarnings,
        };
    }

    public async ValueTask RequireUnchangedAsync(CancellationToken cancellationToken = default)
    {
        string actual = await HashAsync(Area.StagedDraPath, Bytes, cancellationToken).ConfigureAwait(false);
        if (actual != StagedSha256)
        {
            throw new InvalidOperationException("The staged DRA changed after selection. Stage a new explicit copy before preparing.");
        }
    }

    internal static async ValueTask<string> HashAsync(string path, long expectedBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedBytes || expectedBytes is < 1 or > MaximumDraBytes)
        {
            throw new InvalidDataException("The DRA length does not match its admitted stage evidence.");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long read = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            read = checked(read + count);
            if (read > expectedBytes)
            {
                throw new InvalidDataException("The DRA grew while its stage evidence was checked.");
            }
            hash.AppendData(buffer, 0, count);
        }
        if (read != expectedBytes)
        {
            throw new InvalidDataException("The DRA changed while its stage evidence was checked.");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CreateNewDirectory(string path)
    {
        bool created = OperatingSystem.IsWindows()
            ? CreateDirectoryW(path, IntPtr.Zero)
            : OperatingSystem.IsLinux()
                ? mkdir(path, 0x1C0) == 0 // Owner-only rwx (0700).
                : throw new PlatformNotSupportedException("DRA staging supports Windows and Linux filesystem checks.");
        if (!created)
        {
            throw new IOException("The stage directory could not be exclusively created.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdir(string path, uint mode);
}
