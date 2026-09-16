using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PD.Simple;

/// <summary>
/// Saves an application-owned WPF window as a PNG and copies both its path and
/// file payload to the Windows clipboard.
/// </summary>
internal static class WindowScreenshot
{
    internal const string ButtonAutomationName =
        "Capture PD Simple window screenshot and copy path";
    internal const string OutputDirectoryVariable = "PD_SIMPLE_SCREENSHOT_DIR";

    internal static string CaptureAndCopyPath(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsLoaded || !window.IsVisible)
        {
            throw new InvalidOperationException(
                "The PD Simple window must be visible before it can be captured.");
        }

        window.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Max(
            1,
            (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(
            1,
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(window);

        string directory = ResolveOutputDirectory();
        Directory.CreateDirectory(directory);
        string path = NextAvailablePath(directory, SafeFileStem(window.Title));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        CopyPathAndFile(path);
        return path;
    }

    private static string ResolveOutputDirectory()
    {
        string? overridden =
            Environment.GetEnvironmentVariable(OutputDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        string pictures =
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrWhiteSpace(pictures))
        {
            return Path.Combine(pictures, "PD Simple", "Screenshots");
        }

        return Path.Combine(Path.GetTempPath(), "PD Simple", "Screenshots");
    }

    private static string NextAvailablePath(string directory, string stem)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string path = Path.Combine(directory, $"{stem}_{timestamp}.png");
        for (int suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(directory, $"{stem}_{timestamp}_{suffix}.png");
        }

        return Path.GetFullPath(path);
    }

    private static string SafeFileStem(string title)
    {
        string source = string.IsNullOrWhiteSpace(title)
            ? "PD Simple"
            : title.Trim();
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string stem = string.Concat(
            source.Select(character => invalid.Contains(character) ? '_' : character));
        stem = string.Join(
            "_",
            stem.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return stem.Length <= 80 ? stem : stem[..80];
    }

    private static void CopyPathAndFile(string path)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, path);
                data.SetData(DataFormats.Text, path);
                data.SetData(DataFormats.FileDrop, new[] { path });
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (ExternalException exception)
            {
                lastError = exception;
                Thread.Sleep(25);
            }
        }

        throw new InvalidOperationException(
            "The PNG was saved, but it could not be copied because the clipboard " +
            $"is busy. {lastError?.Message}",
            lastError);
    }
}
