using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private enum GuidedPhase { Listening, Collecting, Recovering, Suspended }

    private static readonly TimeSpan CollectInterval = TimeSpan.FromMilliseconds(90);
    private GuidedInputSweep? _guidedSweep;
    private JoystickCapture? _guidedCapture;
    private DispatcherTimer? _guidedTimer;
    private GuidedPhase _guidedPhase;
    private long _guidedPhaseStarted;
    private string _guidedPrompt = "";

    private bool IsGuidedLearning => _guidedSweep is not null;

    private void ToggleGuidedLearn(object sender, RoutedEventArgs e)
    {
        if (_guidedSweep is { } running)
        {
            StopGuidedLearn();
            UpdateControl();
            Notify(running.Current is null ? CompletionNote(running)
                : $"Guided learn paused · {running.Learned} learned · {running.Skipped} skipped");
            return;
        }
        if (_hardware is not { } layout || _input is null || ConnectedDevice(layout) is not { } device)
            return;
        if (Layer == GremlinShiftLayer)
            LayerPicker.SelectedItem = HardwareLayers(layout)[0];

        var learned = _plan.Learned.Where(item => item.Address.Device == layout.Id && item.Address.Layer == Layer)
            .Where(item => InputSignal.Parse(item.Token) is not null).ToArray();
        var learnedIds = learned.Select(item => item.Address.Control).ToHashSet(StringComparer.Ordinal);
        // Changing the CM3 mode dial would switch banks during a sweep.
        var steps = layout.Controls.Where(control => !learnedIds.Contains(control.Id) &&
            (layout.ModelId != "virpil-cm3" || control.Location != "mode")).ToArray();
        if (steps.Length == 0)
        {
            Notify($"All inputs for {Layer} are already learned. Use Learn this control to revise one.");
            return;
        }

        StopLearning();
        try
        {
            _guidedCapture = _input.Open(device, new WindowInteropHelper(this).Handle, axes: false);
            _guidedSweep = new GuidedInputSweep(steps, learned.Select(item => InputSignal.Parse(item.Token)!.Token));
            _guidedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            _guidedTimer.Tick += ReadGuidedInput;
            ShowGuidedStep();
        }
        catch (Exception error)
        {
            FailGuidedLearn(error);
        }
    }

    private void SkipGuidedInput(object sender, RoutedEventArgs e)
    {
        if (_guidedSweep is not { Current: not null } sweep)
            return;
        sweep.Advance(null);
        ShowGuidedStep(discardPending: true);
    }

    private void BackGuidedInput(object sender, RoutedEventArgs e)
    {
        if (_hardware is not { } hardware || _guidedSweep?.Back() is not { } step)
            return;
        if (step.Signal is not null)
        {
            var address = new ControlAddress(hardware.Id, step.Control.Id, Layer);
            _plan.Learned.RemoveAll(item => item.Address == address);
            if (step.PreviousToken is { } previous)
                _plan.Learned.Add(new LearnedControl(address, previous));
        }
        ShowGuidedStep(discardPending: true);
    }

    private void ShowGuidedStep(bool discardPending = false)
    {
        if (_guidedSweep is not { } sweep)
            return;
        if (sweep.Current is not { } control)
        {
            _guidedTimer?.Stop();
            SetGuidedPrompt("Use Back to review the last input, or Done to finish.");
            RefreshGuidedLearnControls();
            Notify(CompletionNote(sweep));
            return;
        }

        // Flush only before manual navigation. A normal advance keeps any fresh buffered
        // presses made as soon as the next control is highlighted.
        if (discardPending && _guidedCapture?.TryResetBaseline([]) != true)
        {
            SetGuidedPhase(GuidedPhase.Recovering);
            _guidedPrompt = "Waiting for the controller.";
        }
        else
        {
            SetGuidedPhase(GuidedPhase.Listening);
            _guidedPrompt = GuidedInstruction(control);
        }

        string location = control.Location ?? control.Id;
        if (_view?.Hotspots.Any(spot => LocationOf(spot.ControlId) == location) != true && _hardware is not null)
        {
            _view = _hardware.Views.FirstOrDefault(view => view.Hotspots.Any(spot => LocationOf(spot.ControlId) == location))
                ?? _view;
            DrawViewTabs();
        }

        SelectControl(control, stopLearning: false);
        _guidedTimer?.Start();
        RefreshGuidedLearnControls();
        Notify($"Guided learn · {Layer} · {sweep.Number} of {sweep.Count} · {sweep.Learned} learned · {sweep.Skipped} skipped");
    }

    private void ReadGuidedInput(object? sender, EventArgs e)
    {
        if (_guidedCapture is not { } capture || _guidedSweep is not { Current: { } control } sweep)
            return;
        try
        {
            if (!IsActive)
            {
                if (_guidedPhase != GuidedPhase.Suspended)
                {
                    sweep.ClearCandidates();
                    SetGuidedPhase(GuidedPhase.Suspended);
                    SetGuidedPrompt("Return to this window to continue.");
                }
                return;
            }
            if (_guidedPhase is GuidedPhase.Suspended or GuidedPhase.Recovering)
            {
                if (!capture.TryResetBaseline([]))
                    return;
                SetGuidedPhase(GuidedPhase.Listening);
                SetGuidedPrompt(GuidedInstruction(control));
                return;
            }

            bool axesOnly = control.Kind.Equals("Axis", StringComparison.OrdinalIgnoreCase);
            if (!capture.TryReadChanges(axesOnly, out var changes))
            {
                sweep.ClearCandidates();
                SetGuidedPhase(GuidedPhase.Recovering);
                SetGuidedPrompt("Waiting for the controller. Reconnect it or cancel and rescan.");
                return;
            }
            sweep.Observe(changes);
            if (_guidedPhase == GuidedPhase.Listening)
            {
                if (sweep.Candidates.Count > 0)
                    SetGuidedPhase(GuidedPhase.Collecting);
                else if (changes.Count > 0 && !IsSecondDetent(control))
                    SetGuidedPrompt("That input is already learned. Try the highlighted gesture.");
                return;
            }
            if (Stopwatch.GetElapsedTime(_guidedPhaseStarted) < CollectInterval)
                return;

            if (sweep.Candidates.Count != 1)
            {
                sweep.ClearCandidates();
                if (axesOnly) capture.TryResetAxisBaseline();
                SetGuidedPhase(GuidedPhase.Listening);
                SetGuidedPrompt("Multiple inputs detected. Release them and try that gesture again.");
                return;
            }

            var signal = sweep.Candidates.Single();
            var address = new ControlAddress(_hardware!.Id, control.Id, Layer);
            var previous = _plan.Learned.FirstOrDefault(item => item.Address == address)?.Token;
            sweep.Advance(signal, previous);
            _plan.Remember(address, signal);
            ShowGuidedStep();
        }
        catch (Exception error)
        {
            FailGuidedLearn(error);
        }
    }

    private void SetGuidedPhase(GuidedPhase phase)
    {
        _guidedPhase = phase;
        _guidedPhaseStarted = Stopwatch.GetTimestamp();
    }

    private void SetGuidedPrompt(string prompt)
    {
        _guidedPrompt = prompt;
        InputSummary.Text = prompt;
    }

    private string GuidedInstruction(HardwareControl control)
    {
        string gesture = IsSecondDetent(control) ? "Pull through to the second detent."
            : control.Id == "trigger-first" ? "Squeeze only to the first detent."
            : control.Kind.Equals("Axis", StringComparison.OrdinalIgnoreCase) ? "Move only the highlighted axis."
            : "Use the highlighted control and direction.";
        return Layer switch
        {
            "Shift" => $"Keep your shift held. {gesture}",
            _ when Layer.StartsWith("Mode ", StringComparison.Ordinal) => $"Keep the dial on {Layer}. {gesture}",
            _ => gesture
        };
    }

    private static bool IsSecondDetent(HardwareControl control) =>
        control.Id == "trigger-second" || control.Id.EndsWith("stage-2", StringComparison.Ordinal);

    private string CompletionNote(GuidedInputSweep sweep) =>
        $"Guided learn complete · {Layer} · {sweep.Learned} learned · {sweep.Skipped} skipped";

    private void RefreshGuidedLearnControls()
    {
        if (GuidedLearnButton is null || SkipGuidedInputButton is null || BackGuidedInputButton is null)
            return;
        GuidedLearnButton.Content = _guidedSweep is null ? "Input Configurator" : _guidedSweep.Current is null ? "Done" : "Cancel sweep";
        GuidedLearnButton.IsEnabled = IsGuidedLearning || (_hardware is { } layout && ConnectedDevice(layout) is not null);
        BackGuidedInputButton.Visibility = IsGuidedLearning ? Visibility.Visible : Visibility.Collapsed;
        BackGuidedInputButton.IsEnabled = _guidedSweep?.CanGoBack == true;
        SkipGuidedInputButton.Visibility = _guidedSweep?.Current is not null ? Visibility.Visible : Visibility.Collapsed;
        if (IsGuidedLearning)
        {
            LearnButton.IsEnabled = false;
            ManualInputButton.IsEnabled = false;
            InputSummary.Text = _guidedPrompt;
        }
    }

    private void FailGuidedLearn(Exception error)
    {
        StopGuidedLearn();
        UpdateControl();
        Notify($"Guided learn stopped. {error.Message}");
    }

    private void StopGuidedLearn()
    {
        _guidedTimer?.Stop();
        _guidedTimer = null;
        _guidedCapture?.Dispose();
        _guidedCapture = null;
        _guidedSweep = null;
        RefreshGuidedLearnControls();
    }
}
