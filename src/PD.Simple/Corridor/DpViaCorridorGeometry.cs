namespace PD.Simple.Corridor;

public readonly record struct DpViaCorridorImagePoint(double X, double Y);

/// <summary>Shared measured-corridor geometry and canonical-mil to image projection.</summary>
public static class DpViaCorridorGeometry
{
    public static IReadOnlyList<DpViaCorridorPoint> CorridorCorners(DpViaCorridorFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ValidatePoint(finding.P, nameof(finding));
        ValidatePoint(finding.N, nameof(finding));
        RequirePositiveFinite(finding.HalfWidthMil, nameof(finding));
        RequirePositiveFinite(finding.HalfLengthMil, nameof(finding));
        var dx = finding.N.XMil - finding.P.XMil;
        var dy = finding.N.YMil - finding.P.YMil;
        var magnitude = Math.Max(Math.Abs(dx), Math.Abs(dy));
        RequirePositiveFinite(magnitude, nameof(finding));
        // Normalize before squaring so long or short finite pairs do not overflow
        // or underflow the vector length merely because of the coordinate scale.
        var scaledX = dx / magnitude;
        var scaledY = dy / magnitude;
        var scaledLength = Math.Sqrt(scaledX * scaledX + scaledY * scaledY);
        var axisX = scaledX / scaledLength;
        var axisY = scaledY / scaledLength;
        var centerX = finding.P.XMil / 2 + finding.N.XMil / 2;
        var centerY = finding.P.YMil / 2 + finding.N.YMil / 2;
        var alongX = axisX * finding.HalfLengthMil;
        var alongY = axisY * finding.HalfLengthMil;
        var acrossX = -axisY * finding.HalfWidthMil;
        var acrossY = axisX * finding.HalfWidthMil;
        DpViaCorridorPoint[] corners =
        [
            new(centerX + alongX + acrossX, centerY + alongY + acrossY),
            new(centerX + alongX - acrossX, centerY + alongY - acrossY),
            new(centerX - alongX - acrossX, centerY - alongY - acrossY),
            new(centerX - alongX + acrossX, centerY - alongY + acrossY)
        ];
        foreach (var corner in corners)
        {
            ValidatePoint(corner, nameof(finding));
        }

        for (var i = 0; i < corners.Length; i++)
        {
            if (corners[i] == corners[(i + 1) % corners.Length])
            {
                throw new ArgumentException("Corridor dimensions collapse at this coordinate precision.", nameof(finding));
            }
        }

        return Array.AsReadOnly(corners);
    }

    public static DpViaCorridorImagePoint ProjectToImage(
        DpViaCorridorPoint point, DpViaCorridorBounds bounds, double width, double height)
    {
        ValidatePoint(point, nameof(point));
        ArgumentNullException.ThrowIfNull(bounds);
        RequirePositiveFinite(width, nameof(width));
        RequirePositiveFinite(height, nameof(height));
        if (!double.IsFinite(bounds.MinimumXMil) || !double.IsFinite(bounds.MinimumYMil) ||
            !double.IsFinite(bounds.MaximumXMil) || !double.IsFinite(bounds.MaximumYMil))
        {
            throw new ArgumentException("Viewport bounds must be finite.", nameof(bounds));
        }

        var spanX = bounds.MaximumXMil - bounds.MinimumXMil;
        var spanY = bounds.MaximumYMil - bounds.MinimumYMil;
        RequirePositiveFinite(spanX, nameof(bounds));
        RequirePositiveFinite(spanY, nameof(bounds));
        // Keep subpixel coordinates and out-of-view locations. The renderer owns
        // clipping and image placement; these bounds and points are already mils.
        var x = (point.XMil - bounds.MinimumXMil) / spanX * width;
        var y = (bounds.MaximumYMil - point.YMil) / spanY * height;
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentException("Projected image coordinates must be finite.", nameof(point));
        }

        return new DpViaCorridorImagePoint(x, y);
    }

    private static void ValidatePoint(DpViaCorridorPoint point, string parameter)
    {
        ArgumentNullException.ThrowIfNull(point, parameter);
        if (!double.IsFinite(point.XMil) || !double.IsFinite(point.YMil))
        {
            throw new ArgumentException("Corridor coordinates must be finite.", parameter);
        }
    }

    private static void RequirePositiveFinite(double value, string parameter)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentException("Geometry and image dimensions must be finite and positive.", parameter);
        }
    }
}
