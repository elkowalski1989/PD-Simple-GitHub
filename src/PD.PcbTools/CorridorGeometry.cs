using CircuitHub.AllegroBridge.Engine.Design;

namespace PD.PcbTools;

internal readonly record struct CorridorBox(DesignPoint Center, double Ux, double Uy, double HalfLength, double HalfWidth)
{
    private (double U, double V) Local(DesignPoint point)
    {
        double x = (double)(point.X - Center.X);
        double y = (double)(point.Y - Center.Y);
        return (x * Ux + y * Uy, -x * Uy + y * Ux);
    }

    public bool Contains(DesignPoint point)
    {
        var local = Local(point);
        return Math.Abs(local.U) <= HalfLength && Math.Abs(local.V) <= HalfWidth;
    }

    public (double Entry, double Exit)? Clip(DesignPoint start, DesignPoint end, double nativeScale)
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
    public static double Distance(DesignPoint left, DesignPoint right) =>
        Math.Sqrt(Math.Pow((double)(left.X - right.X), 2) + Math.Pow((double)(left.Y - right.Y), 2));

    public static double DistanceToSegment(DesignPoint point, DesignPoint start, DesignPoint end,
        double nativeScale = 1)
    {
        double dx = (double)(end.X - start.X);
        double dy = (double)(end.Y - start.Y);
        double squaredLength = dx * dx + dy * dy;
        if (squaredLength * nativeScale * nativeScale < 1e-10)
        {
            return Distance(point, start);
        }
        double parameter = Math.Clamp(((double)(point.X - start.X) * dx + (double)(point.Y - start.Y) * dy) / squaredLength, 0, 1);
        return Distance(point, Interpolate(start, end, parameter));
    }

    internal static DesignPoint Interpolate(DesignPoint start, DesignPoint end, double parameter)
    {
        decimal t = checked((decimal)parameter);
        return new(start.X + t * (end.X - start.X), start.Y + t * (end.Y - start.Y));
    }

    public static bool Intersects(DesignBounds left, DesignBounds right) => left.Intersects(right);
    public static bool Contains(DesignBounds box, DesignPoint point) => box.Contains(point);
    public static bool Contains(DesignBounds outer, DesignBounds inner) => outer.Contains(inner);
}
