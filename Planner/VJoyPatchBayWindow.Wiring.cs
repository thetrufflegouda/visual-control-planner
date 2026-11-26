using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VisualControlsBinder.Games;

namespace VisualControlsBinder.Planner;

public partial class VJoyPatchBayWindow
{
    private static readonly Pen WireHitPen = new(Brushes.Transparent, 12);
    private readonly List<(VJoyPatchPort Port, Geometry Geometry)> _wireHits = [];
    private WireDrag? _wireDrag;
    private Path? _draftWire;

    private void PatchMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_scope is null)
            return;
        Point point = e.GetPosition(WireLayer);
        var input = InputAt(point);
        var output = OutputAt(point);
        if (input is not null || output is not null)
        {
            if (PatchArea.CaptureMouse())
            {
                _wireDrag = new WireDrag(input, output, point, Keyboard.Modifiers);
                e.Handled = true;
            }
            return;
        }

        double inputEdge = InputScroll.TranslatePoint(new Point(InputScroll.ActualWidth, 0), WireLayer).X;
        double outputEdge = OutputScroll.TranslatePoint(new Point(), WireLayer).X;
        if (point.X < inputEdge || point.X > outputEdge)
            return;
        var wire = _wireHits.LastOrDefault(hit => hit.Geometry.StrokeContains(WireHitPen, point));
        if (wire.Port is { } port)
        {
            _inputRows[port.Address].Visual.Button.Focus();
            SelectInput(port, Keyboard.Modifiers, revealOutput: false);
            e.Handled = true;
        }
    }

    private void PatchMouseMove(object sender, MouseEventArgs e)
    {
        if (_wireDrag is not { } drag)
            return;
        Point point = e.GetPosition(WireLayer);
        if (_draftWire is null)
        {
            if (Math.Abs(point.X - drag.Start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - drag.Start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            _draftWire = new Path { StrokeThickness = 2.5 };
            Panel.SetZIndex(_draftWire, 2);
            WireLayer.Children.Add(_draftWire);
        }
        DrawDraft(drag, point);
        e.Handled = true;
    }

    private void DrawDraft(WireDrag drag, Point point)
    {
        if (_draftWire is not { } draft)
            return;
        if (drag.Input is { } input)
        {
            var output = OutputAt(point);
            draft.Opacity = input.Signal is not null && output is not null ? 1 : 0.64;
            draft.Data = WireGeometry(SocketCenter(_inputRows[input.Address].Visual),
                output is not null ? SocketCenter(_outputRows[output]) : point);
            draft.SetResourceReference(Shape.StrokeProperty, LayerColor(input.Address.Layer));
        }
        else if (drag.Output is { } output)
        {
            var target = InputAt(point);
            draft.Opacity = target?.Signal is not null ? 1 : 0.64;
            draft.Data = WireGeometry(target is not null ? SocketCenter(_inputRows[target.Address].Visual) : point,
                SocketCenter(_outputRows[output]));
            draft.SetResourceReference(Shape.StrokeProperty, LayerColor(target?.Address.Layer));
        }
    }

    private void PatchMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_wireDrag is not { } drag)
            return;
        bool moved = _draftWire is not null;
        Point point = e.GetPosition(WireLayer);
        EndWireDrag();
        e.Handled = true;
        if (!moved)
        {
            if (drag.Input is { } clicked)
            {
                _inputRows[clicked.Address].Visual.Button.Focus();
                SelectInput(clicked, drag.Modifiers);
            }
            else if (drag.Output is { } clickedOutput)
            {
                _outputRows[clickedOutput].Button.Focus();
                ConnectSelected(clickedOutput);
            }
            return;
        }

        var input = drag.Input ?? InputAt(point);
        var output = drag.Output ?? OutputAt(point);
        if (input is null || output is null)
            return;
        _inputRows[input.Address].Visual.Button.Focus();
        SelectInput(input, ModifierKeys.None, revealOutput: false);
        Connect(input, output);
    }

    private void PatchMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_draftWire is null || _wireDrag is not { } drag)
            return;
        var destination = drag.Input is not null ? OutputScroll : InputScroll;
        destination.ScrollToVerticalOffset(destination.VerticalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void PatchCaptureLost(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PatchArea))
            EndWireDrag();
    }

    private void EndWireDrag()
    {
        if (_wireDrag is null)
            return;
        _wireDrag = null;
        if (_draftWire is { } draft)
        {
            WireLayer.Children.Remove(draft);
            _draftWire = null;
        }
        if (PatchArea.IsMouseCaptured)
            PatchArea.ReleaseMouseCapture();
    }

    private void PatchBayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _wireDrag is not null)
        {
            EndWireDrag();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back && RemoveRouteButton.IsEnabled &&
            !KindPicker.IsDropDownOpen && !PhysicalDevicePicker.IsDropDownOpen &&
            !LayerFilter.IsDropDownOpen && !OutputDevicePicker.IsDropDownOpen)
        {
            RemoveSelectedRoutes();
            e.Handled = true;
        }
    }

    private VJoyPatchPort? InputAt(Point point) => ViewportContains(InputScroll, point)
        ? _inputRows.Values.FirstOrDefault(row => row.Port.HasSocket && SocketContains(row.Visual, point)).Port : null;

    private GremlinOutput? OutputAt(Point point) => ViewportContains(OutputScroll, point)
        ? _outputRows.FirstOrDefault(row => row.Value.Button.IsEnabled && SocketContains(row.Value, point)).Key : null;

    private bool ViewportContains(ScrollViewer viewport, Point point) =>
        new Rect(viewport.TranslatePoint(new Point(), WireLayer), new Size(viewport.ViewportWidth, viewport.ViewportHeight)).Contains(point);

    private bool SocketContains(PortVisual port, Point point) => port.Socket.IsArrangeValid &&
        new Rect(port.Socket.TranslatePoint(new Point(), WireLayer), port.Socket.RenderSize).Contains(point);

    private Point SocketCenter(PortVisual port) =>
        port.Socket.TranslatePoint(new Point(port.Socket.ActualWidth / 2, port.Socket.ActualHeight / 2), WireLayer);

    private static PathGeometry WireGeometry(Point start, Point end)
    {
        double center = (start.X + end.X) / 2;
        return new PathGeometry([new PathFigure(start,
            [new BezierSegment(new Point(center, start.Y), new Point(center, end.Y), end, true)], false)]);
    }

    private sealed record WireDrag(VJoyPatchPort? Input, GremlinOutput? Output, Point Start, ModifierKeys Modifiers);
}
