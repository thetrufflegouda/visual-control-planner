using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VisualControlsBinder.Hardware.Custom;

internal static class CustomPanelDrawing
{
    private static readonly Brush Housing = new SolidColorBrush(Color.FromRgb(72, 65, 55));
    private static readonly Brush Outline = new SolidColorBrush(Color.FromRgb(167, 151, 129));
    private static readonly Brush Knob = new SolidColorBrush(Color.FromRgb(153, 139, 119));
    private static readonly Brush Recess = new SolidColorBrush(Color.FromRgb(35, 31, 27));
    private static readonly Brush Detent = new SolidColorBrush(Color.FromRgb(183, 199, 156));
    private static readonly Brush Spring = new SolidColorBrush(Color.FromRgb(241, 189, 118));

    public static Canvas CreatePanel(CustomController custom, Brush fill, Brush outline)
    {
        var surface = new Canvas { Width = custom.Width, Height = custom.Height };
        surface.Children.Add(new Border
        {
            Width = surface.Width, Height = surface.Height, Background = fill,
            BorderBrush = outline, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(custom.Shape == CustomPanelShape.RoundedRectangle ? 20 : 2)
        });
        foreach (var part in custom.Parts)
        {
            double width = part.Width * surface.Width;
            double height = part.Height * surface.Height;
            Place(surface, CreatePart(part.Kind, part.QuarterTurns), part.X * surface.Width - width / 2,
                part.Y * surface.Height - height / 2, width, height);
        }
        return surface;
    }

    public static FrameworkElement CreatePart(CustomPartKind kind, int quarterTurns = 0)
    {
        var size = CustomPart.DefaultSize(kind);
        var drawing = new Canvas { Width = size.Width, Height = size.Height, IsHitTestVisible = false };
        if (quarterTurns != 0)
            drawing.LayoutTransform = new RotateTransform(90 * (quarterTurns % 4));
        if (CustomPart.Contacts(kind) is { } contacts)
            DrawToggle(drawing, contacts);
        else
        {
            switch (kind)
            {
                case CustomPartKind.Axis:
                    DrawSlider(drawing);
                    break;
                case CustomPartKind.Thumbstick:
                    DrawThumbstick(drawing);
                    break;
                case CustomPartKind.FourWayHat:
                    DrawHat(drawing);
                    break;
                case CustomPartKind.Rocker:
                    DrawRocker(drawing);
                    break;
                case CustomPartKind.Encoder or CustomPartKind.RotaryAxis or CustomPartKind.FivePositionSelector:
                    DrawDial(drawing, kind);
                    break;
                default:
                    DrawButton(drawing, kind == CustomPartKind.LatchingButton);
                    break;
            }
        }

        return new Viewbox { Child = drawing, Stretch = Stretch.Fill, IsHitTestVisible = false };
    }

    private static void DrawButton(Canvas drawing, bool latching)
    {
        Disc(drawing, Housing, 32, 32, 27, Outline);
        Disc(drawing, Recess, 32, 32, 23);
        Disc(drawing, Knob, 32, 31, 19, latching ? Detent : null);
        if (latching)
            Bar(drawing, Detent, 27, 27, 10, 6, 2);
    }

    private static void DrawDial(Canvas drawing, CustomPartKind kind)
    {
        double center = drawing.Width / 2;
        Disc(drawing, Housing, center, center, center - 4, Outline);
        Disc(drawing, Recess, center, center, center - 9);
        int ticks = kind == CustomPartKind.FivePositionSelector ? 5 : kind == CustomPartKind.RotaryAxis ? 9 : 0;
        for (int tick = 0; tick < ticks; tick++)
        {
            double angle = (135.0 + tick * 270.0 / (ticks - 1)) * Math.PI / 180;
            double radius = center - 8;
            Disc(drawing, Outline, center + radius * Math.Cos(angle), center + radius * Math.Sin(angle), 1.6);
        }
        Disc(drawing, Knob, center, center, center - (ticks == 0 ? 12 : 15), Outline);
        if (kind == CustomPartKind.FivePositionSelector)
        {
            Bar(drawing, Recess, center - 6, center - 18, 12, 36, 4);
            Bar(drawing, Detent, center - 2, center - 15, 4, 9, 1);
        }
        else
            Bar(drawing, kind == CustomPartKind.RotaryAxis ? Detent : Housing, center - 2, center - 16, 4, 15, 2);
    }

    private static void DrawSlider(Canvas drawing)
    {
        Bar(drawing, Housing, 4, 3, 40, 82, 6, Outline);
        Bar(drawing, Recess, 20, 11, 8, 66, 3);
        Bar(drawing, Knob, 22, 12, 4, 64, 2);
        Bar(drawing, Outline, 10, 35, 28, 18, 4);
        Bar(drawing, Housing, 15, 43, 18, 2, 1);
    }

    private static void DrawToggle(Canvas drawing, CustomPart.SwitchContacts contacts)
    {
        double x = drawing.Width / 2;
        double y = drawing.Height / 2;
        Bar(drawing, Housing, x - 15, 4, 30, drawing.Height - 8, 7, Outline);
        Bar(drawing, Recess, x - 5, 12, 10, drawing.Height - 24, 5);
        Contact(y - 16, contacts.Up);
        Contact(y + 16, contacts.Down);
        if (contacts.Center is { } center)
            Disc(drawing, center == "OFF" ? Recess : Detent, x - 10, y, 2.4, Outline);

        Disc(drawing, Recess, x, y, 7.5, Outline);
        Bar(drawing, Knob, x - 4, y - 12, 8, 14, 3, Outline);
        Disc(drawing, Outline, x, y - 10, 4);

        void Contact(double vertical, string state)
        {
            Disc(drawing, state == "OFF" ? Recess : Detent, x - 10, vertical, 2.7, Outline);
            if (!state.StartsWith('('))
                return;
            drawing.Children.Add(new Polyline
            {
                Stroke = Spring, StrokeThickness = 1.4,
                Points = new PointCollection([new(x + 7, vertical + 2), new(x + 9, vertical - 2),
                    new(x + 11, vertical + 2), new(x + 13, vertical - 2)])
            });
        }
    }

    private static void DrawThumbstick(Canvas drawing)
    {
        Disc(drawing, Housing, 37, 37, 33, Outline);
        Disc(drawing, Recess, 37, 37, 27);
        Disc(drawing, Knob, 37, 36, 20, Outline);
        Disc(drawing, Housing, 37, 36, 15);
        Disc(drawing, Recess, 37, 35, 11);
        foreach (var point in new Point[] { new(37, 25), new(47, 35), new(37, 45), new(27, 35) })
            Disc(drawing, Knob, point.X, point.Y, 1.5);
    }

    private static void DrawHat(Canvas drawing)
    {
        Disc(drawing, Housing, 32, 32, 27, Outline);
        drawing.Children.Add(new Polygon
        {
            Fill = Knob, Stroke = Outline, StrokeThickness = 1,
            Points = new PointCollection([new(26, 13), new(38, 13), new(38, 26), new(51, 26), new(51, 38),
                new(38, 38), new(38, 51), new(26, 51), new(26, 38), new(13, 38), new(13, 26), new(26, 26)])
        });
        Disc(drawing, Recess, 32, 32, 5, Outline);
    }

    private static void DrawRocker(Canvas drawing)
    {
        Bar(drawing, Housing, 5, 4, 38, 62, 6, Outline);
        Bar(drawing, Knob, 10, 10, 28, 49, 4);
        Bar(drawing, Recess, 10, 35, 28, 23, 3);
        Disc(drawing, Recess, 24, 22, 3);
        Disc(drawing, Housing, 24, 46, 3, Outline);
    }

    private static void Disc(Canvas canvas, Brush fill, double x, double y, double radius, Brush? stroke = null) =>
        Place(canvas, new Ellipse { Fill = fill, Stroke = stroke, StrokeThickness = 1.5 },
            x - radius, y - radius, radius * 2, radius * 2);

    private static void Bar(Canvas canvas, Brush fill, double x, double y, double width, double height, double corner, Brush? stroke = null) =>
        Place(canvas, new Rectangle { Fill = fill, Stroke = stroke, StrokeThickness = 1.5, RadiusX = corner, RadiusY = corner },
            x, y, width, height);

    private static void Place(Canvas canvas, FrameworkElement shape, double x, double y, double width, double height)
    {
        shape.Width = width;
        shape.Height = height;
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        canvas.Children.Add(shape);
    }
}
