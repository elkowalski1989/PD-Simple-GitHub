// Raw Host stdio probe variant: hello, read frames briefly, then close stdin
// so the host drains and exits naturally; surfaces the host's exit code and
// stderr (worker faults hide while a client stays connected).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class RawHostOnceProbe
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

        var stderrTask = host.StandardError.ReadToEndAsync();
        try
        {
            using var readWindow = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!readWindow.Token.IsCancellationRequested)
            {
                string? envelope = await ReadFrameAsync(host.StandardOutput.BaseStream, readWindow.Token);
                if (envelope is null)
                {
                    Console.WriteLine("STDOUT closed by host.");
                    break;
                }

                Console.WriteLine("FRAME: " + envelope);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Read window elapsed; closing stdin.");
        }

        host.StandardInput.Close();
        Console.WriteLine("stdin closed; draining stdout for the shutdown frame...");
        try
        {
            using var drainWindow = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!drainWindow.Token.IsCancellationRequested)
            {
                string? envelope = await ReadFrameAsync(host.StandardOutput.BaseStream, drainWindow.Token);
                if (envelope is null)
                {
                    Console.WriteLine("STDOUT closed by host.");
                    break;
                }

                Console.WriteLine("DRAIN FRAME: " + envelope);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Drain window elapsed.");
        }

        bool exited = host.WaitForExit(TimeSpan.FromSeconds(15));
        Console.WriteLine($"Host exited naturally: {exited} code={(exited ? host.ExitCode : -1)}.");
        try
        {
            string errors = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("STDERR: " + (string.IsNullOrWhiteSpace(errors) ? "(empty)" : errors.Trim()));
        }
        catch (Exception exception)
        {
            Console.WriteLine("STDERR read failed: " + exception.Message);
        }

        if (!host.HasExited)
        {
            host.Kill(entireProcessTree: true);
        }

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
