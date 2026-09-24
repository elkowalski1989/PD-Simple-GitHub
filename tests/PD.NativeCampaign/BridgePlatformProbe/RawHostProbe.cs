// Raw Host stdio probe: speaks the framed-JSON protocol directly so the
// Host's unmapped fault/license frames are visible (the SDK maps them to
// safe messages). Usage: Probe --raw <host-exe> <bridge-dir> [timeout-sec].
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class RawHostProbe
{
    public static async Task<int> RunAsync(string hostExe, string bridgeDirectory, int timeoutSeconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var startInfo = new ProcessStartInfo
        {
            FileName = hostExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--stdio-v1");
        string? tempRoot = Path.GetDirectoryName(Path.GetFullPath(bridgeDirectory));
        startInfo.Environment["CIRCUITHUB_ALLEGRO_BRIDGE_HOST_TEMP_ROOT"] = tempRoot;

        using var host = Process.Start(startInfo);
        if (host is null)
        {
            Console.WriteLine("FAILED host did not start.");
            return 1;
        }

        Console.WriteLine($"Host PID {host.Id}; sending hello for {bridgeDirectory}.");
        var hello = new
        {
            protocol_version = 1,
            kind = "hello",
            correlation_id = Guid.NewGuid().ToString("D"),
            payload = new
            {
                bridge_directory = bridgeDirectory,
                license_key = (string?)null,
                use_bundled_license = true,
                client_version = "1.13.0",
                expected_session = (object?)null,
            },
        };
        byte[] frame = JsonSerializer.SerializeToUtf8Bytes(hello);
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, frame.Length);
        await host.StandardInput.BaseStream.WriteAsync(length, timeout.Token);
        await host.StandardInput.BaseStream.WriteAsync(frame, timeout.Token);
        await host.StandardInput.BaseStream.FlushAsync(timeout.Token);

        var stderr = host.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            while (!timeout.Token.IsCancellationRequested)
            {
                string? envelope = await ReadFrameAsync(host.StandardOutput.BaseStream, timeout.Token);
                if (envelope is null)
                {
                    Console.WriteLine("STDOUT closed by host.");
                    break;
                }

                Console.WriteLine("FRAME: " + envelope);
                using var document = JsonDocument.Parse(envelope);
                string kind = document.RootElement.GetProperty("kind").GetString() ?? string.Empty;
                if (kind is "error")
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("TIMEOUT waiting for host frames.");
        }

        try
        {
            string errors = await stderr;
            if (!string.IsNullOrWhiteSpace(errors))
            {
                Console.WriteLine("STDERR: " + errors.Trim());
            }
        }
        catch
        {
        }

        if (!host.HasExited)
        {
            host.Kill(entireProcessTree: true);
        }

        Console.WriteLine($"Host exit: {host.HasExited} code={(host.HasExited ? host.ExitCode : -1)}.");
        return 0;
    }

    private static async Task<string?> ReadFrameAsync(Stream input, CancellationToken token)
    {
        byte[] lengthBytes = new byte[4];
        int first = await input.ReadAsync(lengthBytes.AsMemory(0, 1), token);
        if (first == 0)
        {
            return null;
        }

        await ReadExactlyAsync(input, lengthBytes.AsMemory(1), token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0 || length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Bad frame length " + length + ".");
        }

        byte[] bytes = new byte[length];
        await ReadExactlyAsync(input, bytes, token);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken token)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..], token);
            if (read == 0)
            {
                throw new EndOfStreamException("Frame ended early.");
            }

            offset += read;
        }
    }
}
