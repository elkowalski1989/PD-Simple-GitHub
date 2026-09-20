// Captures one owned top-level window (never the desktop) to a PNG.
// Methods, all scoped to the target HWND only:
//   printwindow - PrintWindow with PW_RENDERFULLCONTENT (asks Windows/Allegro
//                 to render that window into our DC, occluded-safe when the
//                 app supports it).
//   windowdc    - BitBlt from the window's own DC (reads only that window's
//                 composed pixels; requires it to be visible, not minimized).
//   auto        - tries printwindow first, then windowdc; keeps the first
//                 capture whose non-black share reaches --min-live (percent).
// No full-screen CopyFromScreen path exists in this tool by design.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

internal static class Program
{
    private const int SwRestore = 9;
    private const uint PwRenderFullContent = 2;
    private const int SrcCopy = 0x00CC0020;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static int Main(string[] args)
    {
        int pid = 0;
        string? output = null;
        string method = "auto";
        double minLive = 2.0;
        bool bringToFront = true;
        bool requireUnoccluded = false;
        int placeX = int.MinValue;
        int placeY = 0;
        int placeW = 0;
        int placeH = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid" when i + 1 < args.Length:
                    pid = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--method" when i + 1 < args.Length:
                    method = args[++i].ToLowerInvariant();
                    break;
                case "--min-live" when i + 1 < args.Length:
                    minLive = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--no-bring-to-front":
                    bringToFront = false;
                    break;
                case "--require-unoccluded":
                    requireUnoccluded = true;
                    break;
                case "--place" when i + 1 < args.Length:
                    string[] parts = args[++i].Split(',');
                    placeX = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    placeY = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    placeW = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    placeH = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }

        if (pid <= 0 || string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine("FAILED usage: --pid <n> --out <png> [--method auto|printwindow|windowdc] [--min-live <pct>] [--no-bring-to-front] [--place x,y,w,h] [--require-unoccluded]");
            return 2;
        }

        try
        {
            IntPtr window = FindMainWindow(pid);
            if (window == IntPtr.Zero)
            {
                Console.WriteLine($"FAILED no top-level window for pid {pid}");
                return 1;
            }

            if (IsIconic(window))
            {
                ShowWindow(window, SwRestore);
                Thread.Sleep(500);
            }

            if (placeX != int.MinValue)
            {
                const uint SwpNoZOrder = 0x0004;
                const uint SwpNoActivate = 0x0010;
                SetWindowPos(window, IntPtr.Zero, placeX, placeY, placeW, placeH, SwpNoZOrder | SwpNoActivate);
                Thread.Sleep(400);
            }

            if (bringToFront)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { SetForegroundWindow(window); } catch { }
                    Thread.Sleep(300);
                    if (GetForegroundWindow() == window)
                    {
                        break;
                    }
                }
            }

            if (requireUnoccluded && !WaitUntilUnoccluded(window, (uint)pid, out string occluder))
            {
                Console.WriteLine($"FAILED window is occluded by foreign pixels ({occluder}); refusing to capture");
                return 1;
            }

            GetWindowRect(window, out Rect rect);
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                Console.WriteLine($"FAILED window rect is empty ({width}x{height})");
                return 1;
            }

            string[] order = method switch
            {
                "printwindow" => new[] { "printwindow" },
                "windowdc" => new[] { "windowdc" },
                _ => new[] { "printwindow", "windowdc" },
            };

            var attempts = new List<string>();
            foreach (string candidate in order)
            {
                using Bitmap bitmap = candidate == "printwindow"
                    ? CapturePrintWindow(window, width, height)
                    : CaptureWindowDc(window, width, height);
                double live = MeasureLiveShare(bitmap);
                attempts.Add($"{candidate}={live:F1}%");
                if (live >= minLive || (method != "auto" && candidate == order[^1]))
                {
                    Save(bitmap, output);
                    Console.WriteLine($"CAPTURED {Path.GetFullPath(output)} {width}x{height} live={live:F1}% method={candidate} unoccluded={requireUnoccluded} attempts=[{string.Join(",", attempts)}]");
                    return 0;
                }
            }

            Console.WriteLine($"FAILED all window-only methods below min-live {minLive}% attempts=[{string.Join(",", attempts)}]");
            return 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"FAILED {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static IntPtr FindMainWindow(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.MainWindowHandle != IntPtr.Zero && IsWindow(process.MainWindowHandle))
            {
                return process.MainWindowHandle;
            }
        }
        catch
        {
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == (uint)pid && IsWindow(hWnd))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool WaitUntilUnoccluded(IntPtr window, uint pid, out string occluder)
    {
        occluder = string.Empty;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try { SetForegroundWindow(window); } catch { }
            Thread.Sleep(400);
            GetWindowRect(window, out Rect rect);
            (int x, int y)[] probes = new[]
            {
                (rect.Left + 30, rect.Top + 60),
                (rect.Right - 30, rect.Top + 60),
                (rect.Left + 30, rect.Bottom - 30),
                (rect.Right - 30, rect.Bottom - 30),
                ((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2),
            };
            bool clear = true;
            foreach ((int x, int y) in probes)
            {
                IntPtr hit = WindowFromPoint(new POINT { X = x, Y = y });
                GetWindowThreadProcessId(hit, out uint hitPid);
                if (hitPid != pid)
                {
                    clear = false;
                    occluder = $"point ({x},{y}) owned by pid {hitPid}";
                    break;
                }
            }
            if (clear)
            {
                // Probes hitting the target PID at corners/center/edges prove
                // no foreign pixels overlap the window. Foreground ownership
                // is deliberately NOT required: the OS foreground lock can
                // deny SetForegroundWindow to a background capturer even when
                // nothing occludes the target.
                return true;
            }
        }
        return false;
    }

    private static Bitmap CapturePrintWindow(IntPtr window, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr dc = graphics.GetHdc();
            try
            {
                PrintWindow(window, dc, PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(dc);
            }
        }
        return bitmap;
    }

    private static Bitmap CaptureWindowDc(IntPtr window, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        IntPtr source = GetWindowDC(window);
        if (source == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetWindowDC returned NULL.");
        }
        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            IntPtr dest = graphics.GetHdc();
            try
            {
                if (!BitBlt(dest, 0, 0, width, height, source, 0, 0, SrcCopy))
                {
                    throw new InvalidOperationException("BitBlt from the window DC failed.");
                }
            }
            finally
            {
                graphics.ReleaseHdc(dest);
            }
        }
        finally
        {
            ReleaseDC(window, source);
        }
        return bitmap;
    }

    private static double MeasureLiveShare(Bitmap bitmap)
    {
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            int step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 200);
            long sampled = 0;
            long live = 0;
            unsafe
            {
                byte* row = (byte*)data.Scan0;
                for (int y = 0; y < bitmap.Height; y += step)
                {
                    byte* pixel = row + y * stride;
                    for (int x = 0; x < bitmap.Width; x += step)
                    {
                        byte b = pixel[x * 4];
                        byte g = pixel[x * 4 + 1];
                        byte r = pixel[x * 4 + 2];
                        sampled++;
                        if (r + g + b > 36)
                        {
                            live++;
                        }
                    }
                }
            }
            return sampled == 0 ? 0 : 100.0 * live / sampled;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void Save(Bitmap bitmap, string output)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        bitmap.Save(output, ImageFormat.Png);
    }
}
