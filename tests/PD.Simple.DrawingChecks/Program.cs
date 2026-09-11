using System.Windows;
using System.Windows.Media.Imaging;
using CircuitHub.AllegroBridge.Engine.Live;
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
            CheckAnalysisPublicationIdentity();
            CheckCapturedViewportAdmission();
            int cases = 0;
            foreach (double scale in new[] { 1.0, 1.5, 2.5 })
            {
                CheckDisjointMask(scale, includeIntrusion: false);
                CheckDisjointMask(scale, includeIntrusion: true);
                cases += 2;
            }
            Console.WriteLine($"PASS: {cases} production HUD raster cases: exact pixels inside disjoint clip unions, zero output outside, clipped antialiased graphics, empty visibility, and overlapping rectangles.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            return 1;
        }
    }

    private static void CheckCapturedViewportAdmission()
    {
        var bounds = new DpViaCorridorBounds(-10, -20, 100, 200);
        if (!DpViaCorridorNativeCapture.Matches(new(-10, -20, 100, 200, "mils", 1), bounds) ||
            !DpViaCorridorNativeCapture.Matches(new(-0.254m, -0.508m, 2.54m, 5.08m, "millimeters", 1), bounds) ||
            DpViaCorridorNativeCapture.Matches(new(-9, -20, 100, 200, "mils", 1), bounds) ||
            DpViaCorridorNativeCapture.Matches(new(-10, -20, 100, 200, "unknown", 1), bounds))
        {
            throw new InvalidOperationException("Scoped capture lost exact serialized-viewport or unit admission.");
        }
        Console.WriteLine("PASS: SDK-owned captures retain exact tool viewport admission and physical mil/mm equivalence.");
    }

    private static void CheckAnalysisPublicationIdentity()
    {
        foreach (long generation in new long[] { 18, 739 })
        {
            string session = "publication-session-" + generation;
            string design = "changed-design-" + generation;
            var document = new WorkspaceDocumentIdentity(
                session,
                SessionGeneration: 1,
                BoardGeneration: generation,
                ProcessId: null,
                Design: design,
                ProtocolVersion: "25");
            var result = new DpViaCorridorResult(DpViaCorridorResult.CurrentSchema, "complete",
                generation, design, "mils", "mils", "unused.rpt", null,
                false, 0, 0, 0, 0, 0, 0, 0, false, []);
            var analysis = new DpViaCorridorAnalysis(document, result, 7);
            if (!analysis.IsCurrentFor(session, generation, 7) ||
                analysis.IsCurrentFor(session, generation, 8) ||
                analysis.IsCurrentFor(session, generation + 1, 7) ||
                analysis.IsCurrentFor(session + "-replacement", generation, 7))
            {
                throw new InvalidOperationException("Historical publication identity was admitted as current.");
            }
            var newAnalysis = analysis with { CatalogGeneration = 8 };
            if (!newAnalysis.IsCurrentFor(session, generation, 8))
            {
                throw new InvalidOperationException("A new analysis under the current catalog was rejected.");
            }
        }
        Console.WriteLine("PASS: analysis publication identity rejects changed catalogs, boards and sessions; a new analysis restores navigation eligibility.");
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
        byte[] full = Pixels(Rasterize(fullClip));
        byte[] clipped = Pixels(Rasterize(disjointClips));

        int leftOpaquePixels = 0;
        int rightOpaquePixels = 0;
        int interruptedHudPixels = 0;
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
                if (!visible && full[offset + 3] != 0)
                {
                    interruptedHudPixels++;
                }
            }
        }
        if (leftOpaquePixels == 0 || rightOpaquePixels == 0 || interruptedHudPixels == 0)
        {
            throw new InvalidOperationException("The HUD did not exercise both visible regions and the clipped antialiased-graphics gap.");
        }

        byte[] empty = Pixels(Rasterize(Array.Empty<Int32Rect>()));
        if (empty.Any(value => value != 0))
        {
            throw new InvalidOperationException("An empty SDK visibility union retained positional pixels.");
        }

        Int32Rect[] overlappingClips = [new(0, 0, 220, Height), new(100, 0, 220, Height)];
        byte[] overlapping = Pixels(Rasterize(overlappingClips));
        if (!full.SequenceEqual(overlapping))
        {
            throw new InvalidOperationException("Overlapping visible rectangles altered the full-union raster.");
        }

        BitmapSource Rasterize(IReadOnlyList<Int32Rect> clips)
        {
            var hud = new BoardOverlayHud();
            var size = new Size(Width / scale, Height / scale);
            hud.Measure(size);
            hud.Arrange(new Rect(size));
            hud.UpdateLayout();
            // Native projection is deliberately outside this raster test. These
            // physical anchors become HUD DIPs exactly once, as in production.
            Point[] local = projectedPoints.Select(point =>
                new Point(point.X / scale, point.Y / scale)).ToArray();
            return hud.RasterizeCorridor(Width, Height, finding, local, scale, clips);
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
