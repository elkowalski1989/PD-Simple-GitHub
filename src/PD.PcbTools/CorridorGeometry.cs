using CircuitHub.AllegroBridge;

namespace PD.PcbTools;

internal readonly record struct CorridorBox(AllegroPcbPoint Center, double Ux, double Uy, double HalfLength, double HalfWidth)
{
    private (double U, double V) Local(AllegroPcbPoint point)
    {
        double x = point.X - Center.X;
        double y = point.Y - Center.Y;
        return (x * Ux + y * Uy, -x * Uy + y * Ux);
    }

    public bool Contains(AllegroPcbPoint point)
    {
        var local = Local(point);
        return Math.Abs(local.U) <= HalfLength && Math.Abs(local.V) <= HalfWidth;
    }

    public (double Entry, double Exit)? Clip(AllegroPcbPoint start, AllegroPcbPoint end, double nativeScale)
    {
        var first = Local(start);
        var last = Local(end);
        double entry = 0;
        double exit = 1;
        if (!ClipAxis(first.U, last.U - first.U, HalfLength, nativeScale, ref entry, ref exit) ||
            !ClipAxis(first.V, last.V - first.V, HalfWidth, nativeScale, ref entry, ref exit))
        {
            return null;
        }
        return (entry, exit);
    }

    private static bool ClipAxis(double position, double direction, double limit, double nativeScale,
        ref double entry, ref double exit)
    {
        // Retains the reference algorithm's native-unit parallel threshold.
        if (Math.Abs(direction * nativeScale) < 1e-10)
        {
            return Math.Abs(position) <= limit;
        }
        double first = (-limit - position) / direction;
        double second = (limit - position) / direction;
        entry = Math.Max(entry, Math.Min(first, second));
        exit = Math.Min(exit, Math.Max(first, second));
        return entry <= exit;
    }
}

public static class CorridorGeometry
{
    public static double Distance(AllegroPcbPoint left, AllegroPcbPoint right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    public static double DistanceToSegment(AllegroPcbPoint point, AllegroPcbPoint start, AllegroPcbPoint end,
        double nativeScale = 1)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double squaredLength = dx * dx + dy * dy;
        if (squaredLength * nativeScale * nativeScale < 1e-10)
        {
            return Distance(point, start);
        }
        double parameter = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / squaredLength, 0, 1);
        return Distance(point, Interpolate(start, end, parameter));
    }

    internal static AllegroPcbPoint Interpolate(AllegroPcbPoint start, AllegroPcbPoint end, double parameter) =>
        new(start.X + parameter * (end.X - start.X), start.Y + parameter * (end.Y - start.Y));

    public static bool Intersects(AllegroPcbBounds left, AllegroPcbBounds right) =>
        left.Minimum.X <= right.Maximum.X && left.Maximum.X >= right.Minimum.X &&
        left.Minimum.Y <= right.Maximum.Y && left.Maximum.Y >= right.Minimum.Y;

    public static bool Contains(AllegroPcbBounds box, AllegroPcbPoint point) =>
        point.X >= box.Minimum.X && point.X <= box.Maximum.X && point.Y >= box.Minimum.Y && point.Y <= box.Maximum.Y;

    public static bool Contains(AllegroPcbBounds outer, AllegroPcbBounds inner) =>
        Contains(outer, inner.Minimum) && Contains(outer, inner.Maximum);
}
