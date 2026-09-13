using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PD.Simple;

/// <summary>
/// Tool-specific screen HUD. It receives already validated/projected positions;
/// it never acquires a canvas, derives board coordinates or grants drawing authority.
/// </summary>
internal sealed class BoardOverlayHud : FrameworkElement
{
    internal static readonly Brush ValidBrush = FrozenBrush("#35D07F");
    internal static readonly Brush InvalidBrush = FrozenBrush("#FF647C");
    internal static readonly Brush NeutralBrush = FrozenBrush("#8CA2B8");
    private static readonly Brush FirstPickBrush = FrozenBrush("#4DA3FF");
    private static readonly Brush TextBrush = FrozenBrush("#F4F7FA");
    private static readonly Brush BackgroundBrush = FrozenBrush("#EE0B121A");

    private enum HudMode
    {
        None,
        Picking,
        Completion
    }
    private HudMode _mode;
    private Point? _cursor;
    private Point? _firstPick;
    private Brush _cursorBrush = NeutralBrush;
    private string _cursorLabel = string.Empty;
    private string? _measurement;
    private string? _firstPickLabel;
    private bool _highContrast;

    internal BoardOverlayHud()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
    }

    internal void ShowPicking(Point? cursor, Brush? color, string label,
        string? measurement, Point? firstPick, string? firstPickLabel, bool highContrast)
    {
        _mode = HudMode.Picking;
        Clip = null;
        _cursor = cursor;
        _cursorBrush = color ?? NeutralBrush;
        _cursorLabel = label;
        _measurement = measurement;
        _firstPick = firstPick;
        _firstPickLabel = firstPickLabel;
        _highContrast = highContrast;
        InvalidateVisual();
    }

    internal void ShowCompletion(Point start, Point end, string label, bool highContrast)
    {
        _mode = HudMode.Completion;
        Clip = null;
        _firstPick = start;
        _cursor = end;
        _cursorLabel = label + " · endpoints only";
        _highContrast = highContrast;
        InvalidateVisual();
    }

    internal void Clear()
    {
        _mode = HudMode.None;
        Clip = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 16 || ActualHeight <= 16)
        {
            return;
        }
        if (_mode is not (HudMode.Picking or HudMode.Completion))
        {
            return;
        }

        Brush current = _highContrast ? SystemColors.HighlightBrush :
            _mode == HudMode.Completion ? ValidBrush : _cursorBrush;
        Brush accepted = _highContrast ? SystemColors.HighlightBrush :
            _mode == HudMode.Completion ? ValidBrush : FirstPickBrush;
        double stroke = _highContrast ? 4 : 2.5;
        if (_firstPick is { } first)
        {
            drawing.DrawEllipse(null, new Pen(accepted, stroke), first, 12, 12);
            drawing.DrawEllipse(accepted, null, first, 3, 3);
        }
        if (_cursor is not { } cursor)
        {
            return;
        }
        drawing.DrawEllipse(null, new Pen(current, stroke), cursor, 17, 17);
        string label = _cursorLabel;
        if (_mode == HudMode.Picking && !string.IsNullOrWhiteSpace(_measurement))
        {
            label += "\n" + _measurement;
        }
        Rect card = DrawCard(drawing, label, cursor, new Vector(22, 18), current, 360);
        if (_mode == HudMode.Picking && _firstPick is { } anchor &&
            !string.IsNullOrWhiteSpace(_firstPickLabel))
        {
            Rect badge = CardBounds(_firstPickLabel, anchor, new Vector(16, -34), 260);
            if (!badge.IntersectsWith(card))
            {
                DrawCard(drawing, _firstPickLabel, anchor, new Vector(16, -34), accepted, 260);
            }
        }
    }

    private Rect DrawCard(DrawingContext drawing, string label, Point anchor,
        Vector offset, Brush border, double maximumWidth)
    {
        FormattedText text = FormatText(label, maximumWidth);
        Rect bounds = CardBounds(text, anchor, offset);
        if (bounds.IsEmpty)
        {
            return Rect.Empty;
        }
        drawing.DrawRoundedRectangle(BackgroundBrush, new Pen(border, 1), bounds, 6, 6);
        drawing.DrawText(text, bounds.TopLeft + new Vector(8, 6));
        return bounds;
    }

    private Rect CardBounds(string label, Point anchor, Vector offset, double maximumWidth) =>
        CardBounds(FormatText(label, maximumWidth), anchor, offset);

    private Rect CardBounds(FormattedText text, Point anchor, Vector offset)
    {
        double width = text.Width + 16;
        double height = text.Height + 12;
        if (height > ActualHeight - 16)
        {
            return Rect.Empty;
        }
        double left = anchor.X + offset.X;
        double top = anchor.Y + offset.Y;
        if (left + width > ActualWidth - 8)
        {
            left = anchor.X - width - Math.Abs(offset.X);
        }
        if (top + height > ActualHeight - 8)
        {
            top = anchor.Y - height - 18;
        }
        return new Rect(Math.Max(8, left), Math.Max(8, top), width, height);
    }

    private FormattedText FormatText(string text, double maximumWidth) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI Semibold"), 11, TextBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip)
    {
        MaxTextWidth = Math.Max(1, Math.Min(maximumWidth, ActualWidth - 32)),
        MaxLineCount = 4,
        Trimming = TextTrimming.CharacterEllipsis
    };

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
