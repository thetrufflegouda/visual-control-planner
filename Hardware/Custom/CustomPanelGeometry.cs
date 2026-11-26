namespace VisualControlsBinder.Hardware.Custom;

internal static class CustomPanelGeometry
{
    public const double GridStep = 8;
    public const double MinimumPanel = 120;
    public const double MaximumPanel = 2400;
    private const double MinimumControl = 16;

    public static CustomPart Snap(CustomPart part, double x, double y, double width, double height) =>
        part.At(GridCenter(x, width, part.Width), GridCenter(y, height, part.Height));

    public static CustomPart? OffsetCopy(CustomPart source, double width, double height, IEnumerable<CustomPart> parts)
    {
        if (source.Width > 1 || source.Height > 1)
            return null;
        var original = Snap(source, source.X, source.Y, width, height);
        var occupied = parts.Select(part => Snap(part, part.X, part.Y, width, height))
            .Append(original).Select(part => (part.X, part.Y)).ToHashSet();
        int limit = (int)Math.Ceiling(Math.Max(width, height) / GridStep);
        for (int distance = 1; distance <= limit; distance++)
        {
            for (int offset = distance; offset >= -distance; offset--)
            {
                var candidate = TryPosition(distance, offset) ?? TryPosition(offset, distance)
                    ?? TryPosition(-distance, offset) ?? TryPosition(offset, -distance);
                if (candidate is not null)
                    return candidate;
            }
        }
        return null;

        CustomPart? TryPosition(int stepX, int stepY)
        {
            double x = GridCenter(original.X + stepX * GridStep / width, width, source.Width);
            double y = GridCenter(original.Y + stepY * GridStep / height, height, source.Height);
            return occupied.Contains((x, y)) ? null : source.At(x, y);
        }
    }

    public static double ResizePanel(double requested, double available, double current,
        IEnumerable<CustomPart> parts, bool horizontal)
    {
        double required = parts.Select(part => RequiredExtent(part, current, horizontal)).DefaultIfEmpty().Max();
        double minimum = Math.Max(MinimumPanel, required);
        double maximum = Math.Max(minimum, Math.Min(MaximumPanel, available));
        return SnapExtent(requested, minimum, maximum);
    }

    public static CustomPart Reframe(CustomPart part, double oldWidth, double oldHeight, double width, double height)
    {
        // Positions and sizes in the saved record are fractions of the body. Preserve their logical coordinates.
        double xScale = oldWidth / width;
        double yScale = oldHeight / height;
        return part with
        {
            X = .5 + (part.X - .5) * xScale,
            Y = .5 + (part.Y - .5) * yScale,
            Width = part.Width * xScale,
            Height = part.Height * yScale
        };
    }

    public static CustomPart ResizeControl(CustomPart part, double requested, double panelExtent, bool horizontal)
    {
        double center = (horizontal ? part.X : part.Y) * panelExtent;
        double maximum = 2 * Math.Min(center, panelExtent - center);
        double extent = SnapExtent(requested, Math.Min(MinimumControl, maximum), maximum);
        return horizontal ? part with { Width = extent / panelExtent } : part with { Height = extent / panelExtent };
    }

    public static CustomPart? RotateControl(CustomPart part, double width, double height)
    {
        double rotatedWidth = part.Height * height / width;
        double rotatedHeight = part.Width * width / height;
        if (rotatedWidth > 1 || rotatedHeight > 1)
            return null;
        var rotated = part with
        {
            Width = rotatedWidth, Height = rotatedHeight, QuarterTurns = (part.QuarterTurns + 1) % 4
        };
        var fitted = rotated.At(part.X, part.Y);
        return fitted.X == part.X && fitted.Y == part.Y ? fitted : Snap(rotated, part.X, part.Y, width, height);
    }

    private static double GridCenter(double value, double panel, double fraction)
    {
        double limit = Math.Floor((1 - fraction) * panel / 2 / GridStep) * GridStep;
        return .5 + Math.Clamp(OnGrid((value - .5) * panel), -limit, limit) / panel;
    }

    private static double RequiredExtent(CustomPart part, double panel, bool horizontal)
    {
        double center = horizontal ? part.X : part.Y;
        double size = horizontal ? part.Width : part.Height;
        return panel * (size + 2 * Math.Abs(center - .5));
    }

    private static double SnapExtent(double value, double minimum, double maximum) =>
        Math.Clamp(2 * OnGrid(value / 2), minimum, maximum);

    private static double OnGrid(double value) =>
        Math.Round(value / GridStep, MidpointRounding.AwayFromZero) * GridStep;
}
