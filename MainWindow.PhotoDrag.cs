using System.Windows;
using System.Windows.Input;
using VisualControlsBinder.Hardware;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private PhotoTurn? _photoTurn;

    private sealed class PhotoTurn(HardwareDefinition hardware, int startIndex, Point origin, double stride)
    {
        public HardwareDefinition Hardware { get; } = hardware;
        public Point Origin { get; } = origin;
        public double Stride { get; } = stride;
        public int Steps { get; set; }
        public bool Moved { get; set; }

        public HardwareView ViewAt(int steps)
        {
            var views = Hardware.Views;
            int index = (startIndex + steps) % views.Count;
            return views[index < 0 ? index + views.Count : index];
        }
    }

    private void StartPhotoDrag(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PhotoCanvas))
            return;
        if (BeginPhotoTurn(e.GetPosition(PhotoCanvas)) && PhotoCanvas.CaptureMouse())
        {
            e.Handled = true;
            return;
        }
        _photoTurn = null;
        if (_control is not null && !IsGuidedLearning)
        {
            SelectControl(null);
            e.Handled = true;
        }
    }

    private bool BeginPhotoTurn(Point origin)
    {
        if (_hardware is not { Views.Count: > 1 } hardware || _view is not { } view)
            return false;
        int index = hardware.Views.ToList().IndexOf(view);
        if (index < 0)
            return false;
        _photoTurn = new PhotoTurn(hardware, index, origin, Math.Clamp(PhotoCanvas.ActualWidth / 5, 64, 128));
        return true;
    }

    private void MovePhotoDrag(object sender, MouseEventArgs e)
    {
        if (PhotoCanvas.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            TrackPhotoTurn(e.GetPosition(PhotoCanvas));
    }

    private void TrackPhotoTurn(Point pointer)
    {
        if (_photoTurn is not { } turn)
            return;
        if (!ReferenceEquals(_hardware, turn.Hardware) || _view != turn.ViewAt(turn.Steps))
        {
            EndPhotoTurn();
            return;
        }

        var offset = pointer - turn.Origin;
        if (!turn.Moved && (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(offset.X) < Math.Abs(offset.Y)))
            return;
        turn.Moved = true;

        double distance = -offset.X / turn.Stride;
        // A small overlap around each boundary keeps the photo steady if the pointer jitters.
        if (Math.Abs(distance - turn.Steps) < .58)
            return;
        turn.Steps = (int)Math.Round(distance, MidpointRounding.AwayFromZero);
        ShowPhotoView(turn.ViewAt(turn.Steps));
    }

    private void FinishPhotoDrag(object sender, MouseButtonEventArgs e)
    {
        if (_photoTurn is not { } turn)
            return;
        var pointer = e.GetPosition(PhotoCanvas);
        TrackPhotoTurn(pointer);
        bool click = ReferenceEquals(turn, _photoTurn) && !turn.Moved &&
            Math.Abs(pointer.X - turn.Origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pointer.Y - turn.Origin.Y) < SystemParameters.MinimumVerticalDragDistance;
        EndPhotoTurn();
        if (click && _control is not null && !IsGuidedLearning)
            SelectControl(null);
        e.Handled = true;
    }

    private void CancelPhotoDrag(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PhotoCanvas))
            _photoTurn = null;
    }

    private void CancelPhotoTurnWithEscape(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _photoTurn is null)
            return;
        EndPhotoTurn();
        e.Handled = true;
    }

    private void EndPhotoTurn()
    {
        _photoTurn = null;
        if (PhotoCanvas.IsMouseCaptured)
            PhotoCanvas.ReleaseMouseCapture();
    }
}
