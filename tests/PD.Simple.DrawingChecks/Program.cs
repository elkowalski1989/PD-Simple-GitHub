using System.Windows;
using System.Windows.Media.Imaging;
using PD.Simple;
using PD.Simple.Corridor;

internal static class Program
{
    private const int Width = 320;
    private const int Height = 200;

    [STAThread]
    private static int Main()
    {
        try
        {
            int cases = 0;
            foreach (double scale in new[] { 1.0, 1.5, 2.5 })
            {
                CheckDisjointMask(scale, includeIntrusion: false);
                CheckDisjointMask(scale, includeIntrusion: true);
                cases += 2;
            }
            Console.WriteLine($"PASS: {cases} WPF raster cases: exact pixels inside disjoint clip unions, zero output outside, interrupted wide strokes, empty visibility, and overlapping rectangles.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            return 1;
        }
    }

    private static void CheckDisjointMask(double scale, bool includeIntrusion)
    {
        var finding = new DpViaCorridorFinding(
            "changed-finding-" + scale,
            "test-pair-" + includeIntrusion,
            "changed-aggressor",
            "trace",
            "ETCH/TEST",
            "Signal",
            "MEDIUM",
            new DpViaCorridorPoint(0, 0),
            new DpViaCorridorPoint(10, 0),
            includeIntrusion ? new DpViaCorridorPoint(5, 0) : null,
            0,
            2,
            8);
        var projectedPoints = new List<Point>
        {
            new(40, 60),
            new(280, 60),
            new(280, 140),
            new(40, 140),
            new(70, 100),
            new(250, 100)
        };
        if (includeIntrusion)
        {
            projectedPoints.Add(new Point(160, 100));
        }

        Int32Rect[] fullClip = [new(0, 0, Width, Height)];
        Int32Rect[] disjointClips = [new(0, 0, 110, Height), new(210, 0, 110, Height)];
        byte[] full = Pixels(DpViaCorridorDrawingFrame.Rasterize(
            Width, Height, fullClip, finding, projectedPoints, scale));
        byte[] clipped = Pixels(DpViaCorridorDrawingFrame.Rasterize(
            Width, Height, disjointClips, finding, projectedPoints, scale));

        int leftOpaquePixels = 0;
        int rightOpaquePixels = 0;
        int interruptedStrokePixels = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * Width + x) * 4;
                bool visible = x < 110 || x >= 210;
                for (int channel = 0; channel < 4; channel++)
                {
                    byte expected = visible ? full[offset + channel] : (byte)0;
                    if (clipped[offset + channel] != expected)
                    {
                        throw new InvalidOperationException(
                            $"Mask changed pixel ({x},{y}) channel {channel}, scale={scale}, intrusion={includeIntrusion}.");
                    }
                }
                if (clipped[offset + 3] != 0)
                {
                    if (x < 110)
                    {
                        leftOpaquePixels++;
                    }
                    else if (x >= 210)
                    {
                        rightOpaquePixels++;
                    }
                }
                if (!visible && y is >= 55 and <= 64 && full[offset + 3] != 0)
                {
                    interruptedStrokePixels++;
                }
            }
        }
        if (leftOpaquePixels == 0 || rightOpaquePixels == 0 || interruptedStrokePixels == 0)
        {
            throw new InvalidOperationException("The test geometry did not exercise both visible regions and the clipped wide-stroke gap.");
        }

        byte[] empty = Pixels(DpViaCorridorDrawingFrame.Rasterize(
            Width, Height, Array.Empty<Int32Rect>(), finding, projectedPoints, scale));
        if (empty.Any(value => value != 0))
        {
            throw new InvalidOperationException("An empty SDK visibility union retained positional pixels.");
        }

        Int32Rect[] overlappingClips = [new(0, 0, 220, Height), new(100, 0, 220, Height)];
        byte[] overlapping = Pixels(DpViaCorridorDrawingFrame.Rasterize(
            Width, Height, overlappingClips, finding, projectedPoints, scale));
        if (!full.SequenceEqual(overlapping))
        {
            throw new InvalidOperationException("Overlapping visible rectangles altered the full-union raster.");
        }
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth != Width || bitmap.PixelHeight != Height || bitmap.DpiX != 96 || bitmap.DpiY != 96)
        {
            throw new InvalidOperationException("The raster is not an unresampled 96-DPI physical-pixel bitmap.");
        }
        int stride = Width * 4;
        var bytes = new byte[stride * Height];
        bitmap.CopyPixels(bytes, stride, 0);
        return bytes;
    }
}
