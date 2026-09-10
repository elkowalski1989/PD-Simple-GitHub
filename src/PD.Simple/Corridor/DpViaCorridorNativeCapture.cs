using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Windows;
using PD.Bridge;
using PD.Simple.Corridor;

namespace PD.Simple.Corridor;

/// <summary>
/// Owned current Allegro viewport pixels, bracketed by native bounds and Windows identity readbacks.
/// Layer identifies the selected finding from the verified zoom reply, not an independently sampled
/// display-layer state. The image includes whichever layers are visible at capture time and is not continuously live.
/// </summary>
public sealed record DpViaCorridorNativeCapture(BitmapSource Image, DpViaCorridorBounds Bounds,
    string FindingId, string Layer, DateTimeOffset CapturedAt)
{
    internal static async Task<DpViaCorridorNativeCapture> CaptureAsync(AllegroBridgeSession session,
        AllegroDesktopBinding desktop, DpViaCorridorZoomResult zoom, CancellationToken cancellationToken)
    {
        var initial = session.CaptureObservation();
        var binding = initial.Context.Binding;
        void RequireContext()
        {
            var current = session.CaptureObservation();
            if (!desktop.IsCurrentFor(binding) || !desktop.IsCurrentFor(session.Binding) ||
                !desktop.IsCurrentFor(current.Context.Binding) ||
                current.Context.Binding.BoardGeneration != zoom.BoardGeneration ||
                current.Snapshot.Design != zoom.Design ||
                current.Snapshot.DesignLocator != initial.Snapshot.DesignLocator)
            {
                throw new InvalidOperationException("The board changed before its image could be captured.");
            }
        }
        RequireContext();
        // Reuse the SDK's shared inspection owner. Starting an independent
        // InspectCanvasAsync here would race the overlay observer's inspection.
        // This subscription owns no additional worker and does not pause others.
        using var observer = desktop.ObserveCanvasViews();
        var canvas = await ReadSharedCanvasAsync(observer, cancellationToken);
        if (!canvas.IsAvailable || !desktop.IsCurrentFor(canvas.Binding))
        {
            throw new InvalidOperationException("Allegro canvas unavailable: " + canvas.UnavailableReason);
        }

        var before = await session.CaptureNativeViewAsync(cancellationToken);
        if (before.Result.Sample is not { } first || !Matches(first, zoom.ActualBounds))
        {
            throw new InvalidOperationException("Allegro moved away from the selected crossing before capture.");
        }

        var started = Stopwatch.GetTimestamp();
        RequireContext();
        var pixels = CapturePixels(desktop, canvas);
        var after = await session.CaptureNativeViewAsync(cancellationToken);
        RequireContext();
        if (before.CaptureAgeUpperBound > TimeSpan.FromSeconds(1) ||
            Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(1) ||
            after.Result.Sample is not { } last || !SameView(first, last) ||
            !Matches(last, zoom.ActualBounds))
        {
            throw new InvalidOperationException("The Allegro view changed or expired while capturing its image.");
        }

        ValidateWindows(desktop, canvas);
        return new(pixels, zoom.ActualBounds, zoom.FindingId, zoom.Layer, DateTimeOffset.UtcNow);
    }

    private static async Task<AllegroCanvasInspection> ReadSharedCanvasAsync(
        AllegroCanvasViewObserver observer, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(2));
        var update = observer.Current;
        try
        {
            while (true)
            {
                if (update.Error is { } error)
                {
                    throw new InvalidOperationException("Shared canvas acquisition failed.", error);
                }
                if (update.IsTerminal)
                {
                    throw new InvalidOperationException("The shared canvas observer ended: " + update.Status.Code);
                }
                if (update.IsAvailable && update.View?.Canvas is { IsAvailable: true } canvas)
                {
                    return canvas;
                }
                update = await observer.ReadNextAsync(wait.Token)
                    ?? throw new InvalidOperationException("The shared canvas observer ended before capture.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // This deadline cancels only our notification wait, not native work.
            throw new InvalidOperationException(
                $"The shared canvas did not become available for capture: {update.Status.Code} ({update.Status.Diagnostic}).");
        }
    }

    private static bool SameView(NativeViewSample a, NativeViewSample b) =>
        a.Window.ActiveCanvasId == 0 && a.Window == b.Window && a.Point.Units == b.Point.Units &&
        a.MinimumX == b.MinimumX && a.MinimumY == b.MinimumY &&
        a.MaximumX == b.MaximumX && a.MaximumY == b.MaximumY && a.UnitsPerPixel == b.UnitsPerPixel;

    private static bool Matches(NativeViewSample sample, DpViaCorridorBounds bounds)
    {
        if (sample.Window.ActiveCanvasId != 0)
        {
            return false;
        }

        var scale = sample.Point.Units.ToUpperInvariant() switch
        {
            "MILS" => 1d,
            "MILLIMETERS" => 1d / 0.0254d,
            _ => double.NaN
        };
        // The custom native reply writes %.12g. Match its serialization precision,
        // not a spatial tolerance; native containment/revalidation stays exact.
        double Wire(decimal value) => double.Parse(((double)value * scale)
            .ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return double.IsFinite(scale) && Wire(sample.MinimumX) == bounds.MinimumXMil &&
            Wire(sample.MinimumY) == bounds.MinimumYMil && Wire(sample.MaximumX) == bounds.MaximumXMil &&
            Wire(sample.MaximumY) == bounds.MaximumYMil;
    }

    private static BitmapSource CapturePixels(AllegroDesktopBinding desktop, AllegroCanvasInspection canvas)
    {
        nint previous = SetThreadDpiAwarenessContext(-4);
        if (previous == 0)
        {
            throw new InvalidOperationException("Physical-pixel capture is unavailable.");
        }

        nint windowDc = 0, memoryDc = 0, bitmap = 0, original = 0;
        try
        {
            var window = ValidateWindowsCore(desktop, canvas);
            int width = checked(window.Right - window.Left), height = checked(window.Bottom - window.Top);
            if (width <= 0 || height <= 0 || (long)width * height > 16_000_000)
            {
                throw new InvalidDataException("The owned Allegro image exceeds the bounded capture size.");
            }

            windowDc = GetDC(desktop.TopLevelWindowHandle);
            memoryDc = CreateCompatibleDC(windowDc);
            bitmap = CreateCompatibleBitmap(windowDc, width, height);
            if (windowDc == 0 || memoryDc == 0 || bitmap == 0)
            {
                throw new InvalidOperationException("Cannot allocate the owned-window capture.");
            }

            original = SelectObject(memoryDc, bitmap);
            if (original == 0 || original == -1 || !PrintWindow(desktop.TopLevelWindowHandle, memoryDc, 2))
            {
                throw new InvalidOperationException("Allegro did not supply its window pixels. No desktop fallback is used.");
            }

            if (ValidateWindowsCore(desktop, canvas) != window)
            {
                throw new InvalidOperationException("The Allegro window moved during capture.");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            var crop = new CroppedBitmap(source, new Int32Rect(canvas.Bounds.Left - window.Left,
                canvas.Bounds.Top - window.Top, checked((int)canvas.Bounds.Width), checked((int)canvas.Bounds.Height)));
            crop.Freeze();
            return crop;
        }
        finally
        {
            if (original != 0 && original != -1)
            {
                SelectObject(memoryDc, original);
            }

            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                DeleteDC(memoryDc);
            }

            if (windowDc != 0)
            {
                ReleaseDC(desktop.TopLevelWindowHandle, windowDc);
            }

            SetThreadDpiAwarenessContext(previous);
        }
    }

    private static void ValidateWindows(AllegroDesktopBinding desktop, AllegroCanvasInspection canvas)
    {
        nint previous = SetThreadDpiAwarenessContext(-4);
        if (previous == 0)
        {
            throw new InvalidOperationException("Physical-pixel validation is unavailable.");
        }

        try
        {
            ValidateWindowsCore(desktop, canvas);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private static PixelRect ValidateWindowsCore(AllegroDesktopBinding desktop, AllegroCanvasInspection canvas)
    {
        PixelRect Read(nint handle)
        {
            GetWindowThreadProcessId(handle, out uint processId);
            if (handle == 0 || processId != desktop.AllegroProcessId || !IsWindowVisible(handle) ||
                !GetWindowRect(handle, out var rect))
            {
                throw new InvalidOperationException("The bound Allegro window identity is no longer available.");
            }

            return rect;
        }
        var top = Read(desktop.TopLevelWindowHandle);
        var main = Read(canvas.MainCanvasWindowHandle);
        var surface = Read(canvas.SurfaceWindowHandle);
        if (IsIconic(desktop.TopLevelWindowHandle) || !IsChild(desktop.TopLevelWindowHandle, canvas.MainCanvasWindowHandle) ||
            !IsChild(canvas.MainCanvasWindowHandle, canvas.SurfaceWindowHandle) || main != surface ||
            surface != new PixelRect(canvas.Bounds.Left, canvas.Bounds.Top, canvas.Bounds.Right, canvas.Bounds.Bottom) ||
            GetDpiForWindow(canvas.SurfaceWindowHandle) != canvas.Dpi || surface.Left < top.Left || surface.Top < top.Top ||
            surface.Right > top.Right || surface.Bottom > top.Bottom)
        {
            throw new InvalidOperationException("The Allegro canvas is minimized, moved, or no longer matches its SDK identity.");
        }

        return top;
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out PixelRect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint handle, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint handle, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
