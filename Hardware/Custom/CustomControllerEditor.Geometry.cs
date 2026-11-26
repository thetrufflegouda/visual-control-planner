using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VisualControlsBinder.Hardware.Custom;

internal partial class CustomControllerEditor
{
    private enum Edge { Left, Right, Top, Bottom }

    private sealed record ResizeHandle(Edge Edge, bool IsControl)
    {
        public bool Horizontal => Edge is Edge.Left or Edge.Right;
        public int Direction => Edge is Edge.Left or Edge.Top ? -1 : 1;
    }

    private sealed record ResizeDrag(ResizeHandle Handle, Point Pointer, double InitialExtent, double Scale);
    private const double OutlineThickness = 4;
    private readonly List<Thumb> _resizeHandles = [];
    private double _panelScale = 1;
    private ResizeDrag? _resizeDrag;

    private void CreateResizeHandles()
    {
        foreach (bool part in new[] { false, true })
        {
            foreach (Edge edge in Enum.GetValues<Edge>())
            {
                var handle = new ResizeHandle(edge, part);
                var noun = part ? "control" : "panel";
                string label = $"Resize {noun} from the {edge.ToString().ToLowerInvariant()} edge";
                double length = part ? 17 : 30;
                double thickness = part ? 9 : 11;
                var thumb = new Thumb
                {
                    Tag = handle, Width = handle.Horizontal ? thickness : length,
                    Height = handle.Horizontal ? length : thickness, Focusable = true,
                    Cursor = handle.Horizontal ? Cursors.SizeWE : Cursors.SizeNS,
                    ToolTip = $"{label}. Drag or use the arrow keys.",
                    Style = (Style)FindResource("ResizeThumbStyle")
                };
                if (part)
                    thumb.SetResourceReference(BackgroundProperty, "Accent");
                AutomationProperties.SetName(thumb, label);
                Panel.SetZIndex(thumb, part ? 2 : 1);
                thumb.DragStarted += StartResize;
                thumb.DragDelta += ContinueResize;
                thumb.DragCompleted += FinishResize;
                thumb.KeyDown += ResizeWithArrow;
                _resizeHandles.Add(thumb);
                PanelWorkspace.Children.Add(thumb);
            }
        }
    }

    private void WorkspaceSizeChanged(object sender, SizeChangedEventArgs e) => FitWorkspace();

    private void FitWorkspace()
    {
        if (PanelOutline is null || PanelWorkspace.ActualWidth < 1 || PanelWorkspace.ActualHeight < 1)
            return;
        const double clearance = 46;
        _panelScale = Math.Min(1, Math.Min(
            Math.Max(1, PanelWorkspace.ActualWidth - clearance) / (_width + OutlineThickness),
            Math.Max(1, PanelWorkspace.ActualHeight - clearance) / (_height + OutlineThickness)));
        PositionBody();
    }

    private void PositionBody()
    {
        double frameWidth = (_width + OutlineThickness) * _panelScale;
        double frameHeight = (_height + OutlineThickness) * _panelScale;
        PanelOutline.RenderTransform = new ScaleTransform(_panelScale, _panelScale);
        Canvas.SetLeft(PanelOutline, (PanelWorkspace.ActualWidth - frameWidth) / 2);
        Canvas.SetTop(PanelOutline, (PanelWorkspace.ActualHeight - frameHeight) / 2);
        PositionHandles();
    }

    private void PositionHandles()
    {
        if (_resizeHandles.Count == 0)
            return;
        var part = _parts.FirstOrDefault(item => item.Id == _selectedPart);
        double offsetX = Canvas.GetLeft(PanelOutline);
        double offsetY = Canvas.GetTop(PanelOutline);
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            return;

        foreach (var thumb in _resizeHandles)
        {
            var handle = (ResizeHandle)thumb.Tag;
            thumb.Visibility = handle.IsControl && part is null ? Visibility.Collapsed : Visibility.Visible;
            if (thumb.Visibility == Visibility.Collapsed)
                continue;

            double centerX = PanelWorkspace.ActualWidth / 2;
            double centerY = PanelWorkspace.ActualHeight / 2;
            double halfWidth = (_width + OutlineThickness) * _panelScale / 2;
            double halfHeight = (_height + OutlineThickness) * _panelScale / 2;
            if (handle.IsControl && part is not null)
            {
                centerX = offsetX + (OutlineThickness / 2 + part.X * _width) * _panelScale;
                centerY = offsetY + (OutlineThickness / 2 + part.Y * _height) * _panelScale;
                halfWidth = part.Width * _width * _panelScale / 2;
                halfHeight = part.Height * _height * _panelScale / 2;
            }
            Canvas.SetLeft(thumb, centerX + (handle.Horizontal ? handle.Direction * halfWidth : 0) - thumb.Width / 2);
            Canvas.SetTop(thumb, centerY + (handle.Horizontal ? 0 : handle.Direction * halfHeight) - thumb.Height / 2);
        }
    }

    private void StartResize(object sender, DragStartedEventArgs e)
    {
        var thumb = (Thumb)sender;
        var handle = (ResizeHandle)thumb.Tag;
        thumb.Focus();
        _resizeDrag = new ResizeDrag(handle, Mouse.GetPosition(PanelWorkspace), Extent(handle), _panelScale);
    }

    private void ContinueResize(object sender, DragDeltaEventArgs e)
    {
        if (_resizeDrag is not { } drag)
            return;
        Vector movement = Mouse.GetPosition(PanelWorkspace) - drag.Pointer;
        double delta = drag.Handle.Horizontal ? movement.X : movement.Y;
        ResizeFrom(drag.Handle, drag.InitialExtent + 2 * drag.Handle.Direction * delta / drag.Scale);
    }

    private void FinishResize(object sender, DragCompletedEventArgs e)
    {
        bool panel = _resizeDrag is { Handle.IsControl: false };
        _resizeDrag = null;
        if (!panel)
            return;
        double roomX = PanelWorkspace.ActualWidth - (_width + OutlineThickness) * _panelScale;
        double roomY = PanelWorkspace.ActualHeight - (_height + OutlineThickness) * _panelScale;
        if (Math.Min(roomX, roomY) < 23)
            FitWorkspace();
    }

    private void ResizeWithArrow(object sender, KeyEventArgs e)
    {
        var handle = (ResizeHandle)((Thumb)sender).Tag;
        int motion = (handle.Horizontal, e.Key) switch
        {
            (true, Key.Left) or (false, Key.Up) => -1,
            (true, Key.Right) or (false, Key.Down) => 1,
            _ => 0
        };
        if (motion == 0)
            return;
        ResizeFrom(handle, Extent(handle) + 2 * motion * handle.Direction * CustomPanelGeometry.GridStep);
        e.Handled = true;
    }

    private double Extent(ResizeHandle handle)
    {
        double extent = handle.Horizontal ? _width : _height;
        var control = handle.IsControl ? _parts.FirstOrDefault(part => part.Id == _selectedPart) : null;
        return control is null ? extent : extent * (handle.Horizontal ? control.Width : control.Height);
    }

    private void ResizeFrom(ResizeHandle handle, double requested)
    {
        double extent = handle.Horizontal ? _width : _height;
        if (handle.IsControl)
        {
            int i = _parts.FindIndex(part => part.Id == _selectedPart);
            if (i < 0)
                return;
            _parts[i] = CustomPanelGeometry.ResizeControl(_parts[i], requested, extent, handle.Horizontal);
            Position(_partSurfaces[_parts[i].Id], _parts[i]);
            PositionHandles();
            return;
        }

        double room = handle.Horizontal ? PanelWorkspace.ActualWidth : PanelWorkspace.ActualHeight;
        double available = Math.Max(extent, (room - 18) / _panelScale - OutlineThickness);
        double target = CustomPanelGeometry.ResizePanel(requested, available, extent, _parts, handle.Horizontal);
        double width = handle.Horizontal ? target : _width;
        double height = handle.Horizontal ? _height : target;
        if (width == _width && height == _height)
            return;
        for (int index = 0; index < _parts.Count; index++)
            _parts[index] = CustomPanelGeometry.Reframe(_parts[index], _width, _height, width, height);
        _width = width;
        _height = height;
        ResizeSurface();
        foreach (var part in _parts)
            Position(_partSurfaces[part.Id], part);
        PositionBody();
    }

    private void ResizeSurface()
    {
        PanelSurface.Width = _width;
        PanelSurface.Height = _height;
        PanelOutline.Width = _width + OutlineThickness;
        PanelOutline.Height = _height + OutlineThickness;
        PanelSurface.Background = SnapGrid();
    }

    private Brush SnapGrid()
    {
        const int intervals = 4;
        double step = CustomPanelGeometry.GridStep;
        double tile = intervals * step;
        var drawing = new DrawingGroup();
        var gridColor = ((SolidColorBrush)FindResource("TextSecondary")).Color;
        var fine = new Pen(new SolidColorBrush(Color.FromArgb(27, gridColor.R, gridColor.G, gridColor.B)), .7);
        var major = new Pen(new SolidColorBrush(Color.FromArgb(53, gridColor.R, gridColor.G, gridColor.B)), .8);
        using (var lines = drawing.Open())
        {
            for (int i = 0; i < intervals; i++)
            {
                // A half-pixel offset keeps lines inside their repeated tile.
                double coordinate = i * step + .5;
                var pen = i == 0 ? major : fine;
                lines.DrawLine(pen, new Point(coordinate, 0), new Point(coordinate, tile));
                lines.DrawLine(pen, new Point(0, coordinate), new Point(tile, coordinate));
            }
        }
        var brush = new DrawingBrush(drawing)
        {
            ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(_width / 2 % tile, _height / 2 % tile, tile, tile),
            Viewbox = new Rect(0, 0, tile, tile), TileMode = TileMode.Tile, Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}
