using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private readonly PlanSessionStore _planSessions;
    private string? _planPath;
    private string _savedPlanFingerprint = new BindingPlan().Fingerprint();
    private PlanSession? _restoredPlanSelection;
    private bool _workingPlanUnreadable;
    private bool HasUnsavedPlan => _plan.Fingerprint() != _savedPlanFingerprint;

    private string? RestorePlanSession()
    {
        try
        {
            if (_planSessions.Load() is not { } session)
                return null;
            _plan = session.Plan;
            _planPath = session.FilePath;
            _savedPlanFingerprint = session.SavedFingerprint ?? _savedPlanFingerprint;
            _restoredPlanSelection = session;
            return session.GameId;
        }
        catch (Exception error)
        {
            _workingPlanUnreadable = true;
            Notify($"Could not restore the previous working plan. {error.Message}");
            return null;
        }
    }

    private void RestoreSelectedDeviceAndControl()
    {
        if (_restoredPlanSelection is not { } session)
            return;
        _restoredPlanSelection = null;
        if (DevicePicker.Items.OfType<HardwareChoice>().FirstOrDefault(choice => choice.Definition.Id == session.DeviceId) is not { } device)
            return;

        DevicePicker.SelectedItem = device;
        if (session.Layer is { } layer && LayerPicker.Items.Contains(layer))
            LayerPicker.SelectedItem = layer;
        if (_hardware?.Controls.FirstOrDefault(control => control.Id == session.ControlId) is not { } selected)
            return;

        string location = selected.Location ?? selected.Id;
        if (_view?.Hotspots.Any(spot => LocationOf(spot.ControlId) == location) != true && _hardware is { } hardware)
        {
            _view = hardware.Views.FirstOrDefault(view => view.Hotspots.Any(spot => LocationOf(spot.ControlId) == location)) ?? _view;
            DrawViewTabs();
        }
        SelectControl(selected);
    }

    private void SavePlanSession(object? sender, CancelEventArgs e)
    {
        if (_exportInProgress)
        {
            e.Cancel = true;
            Notify("The binding export is still running.");
            return;
        }
        if (e.Cancel || (_workingPlanUnreadable && _planPath is null && !HasUnsavedPlan))
            return;
        try
        {
            _planSessions.Save(new PlanSession(_plan, _planPath, _savedPlanFingerprint,
                SelectedGame?.Id, _hardware?.Id, _hardware is null ? null : Layer, _control?.Id), HasUnsavedPlan);
        }
        catch (Exception error)
        {
            e.Cancel = !ReviewWindow.Confirm(this, "Could not save the working session",
                $"The working plan could not be saved for next time. {error.Message}", "Close anyway");
        }
    }

    private async void NewPlan(object sender, RoutedEventArgs e)
    {
        if (!ConfirmPlanReplacement("Start a new plan", "Start new plan"))
            return;
        await ReplacePlan(new BindingPlan(), null);
        Notify("Started a new plan.");
    }

    private void SavePlan(object sender, RoutedEventArgs e)
    {
        if (PickPlanSavePath("Save binding plan", Path.GetFileName(_planPath) ?? "Bindings.vbind.json") is not { } path)
            return;
        try
        {
            _plan.Save(path);
            _planPath = path;
            _savedPlanFingerprint = _plan.Fingerprint();
            Notify($"Plan saved to {Path.GetFileName(_planPath)}.");
        }
        catch (Exception error) { Notify(error.Message); }
    }

    private void SharePlan(object sender, RoutedEventArgs e)
    {
        if (PickPlanSavePath("Save shareable plan", "Bindings-share.vbind.json") is not { } path)
            return;
        try
        {
            if (_planPath is not null && Path.GetFullPath(path).Equals(Path.GetFullPath(_planPath), StringComparison.OrdinalIgnoreCase))
            {
                Notify("Choose a separate filename for the shareable copy.");
                return;
            }
            _plan.CreateShareableCopy().Save(path);
            Notify($"Saved a shareable copy to {Path.GetFileName(path)}.");
        }
        catch (Exception error) { Notify(error.Message); }
    }

    private string? PickPlanSavePath(string title, string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title, Filter = "Visual Controls Binder plan (*.vbind.json)|*.vbind.json",
            DefaultExt = ".vbind.json", FileName = fileName,
            InitialDirectory = _planPath is null ? "" : Path.GetDirectoryName(_planPath)
        };
        return dialog.ShowDialog(this) == true && IsNamedPlanPath(dialog.FileName) ? dialog.FileName : null;
    }

    private async void OpenPlan(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open binding plan", Filter = "Visual Controls Binder plan (*.vbind.json)|*.vbind.json",
            InitialDirectory = _planPath is null ? "" : Path.GetDirectoryName(_planPath)
        };
        if (dialog.ShowDialog(this) != true || !IsNamedPlanPath(dialog.FileName)
            || !ConfirmPlanReplacement("Open another plan", "Open plan"))
            return;
        try
        {
            var plan = BindingPlan.Read(dialog.FileName);
            if (plan.HasNetworkOrDevicePaths())
            {
                const string openCopy = "Open a copy without saved paths or device pairings";
                var choice = ReviewWindow.Pick(this, "Plan contains network or device paths",
                    new[] { openCopy, "Use the saved paths and let Windows access them" }, width: 540);
                if (choice is null)
                    return;
                if (choice == openCopy)
                {
                    await ReplacePlan(plan.CreateShareableCopy(), null);
                    Notify($"Opened a shareable copy of {Path.GetFileName(dialog.FileName)}.");
                    return;
                }
            }
            await ReplacePlan(plan, dialog.FileName);
            Notify($"Opened {Path.GetFileName(_planPath)}.");
        }
        catch (Exception error) { Notify(error.Message); }
    }

    private bool ConfirmPlanReplacement(string title, string action) => !HasUnsavedPlan ||
        ReviewWindow.Confirm(this, title, "This replaces the working plan and its unsaved changes. Save the working plan first to keep those changes.", action);

    private bool IsNamedPlanPath(string path)
    {
        if (!_planSessions.IsSnapshotPath(path))
            return true;
        Notify("Choose a separate .vbind.json file. This path holds the automatic working-plan recovery.");
        return false;
    }

    private async Task ReplacePlan(BindingPlan plan, string? path)
    {
        StopLearning();
        _plan = plan;
        _planPath = path;
        _savedPlanFingerprint = plan.Fingerprint();
        DevicePicker.SelectedItem = null;
        ScanDevices();
        await LoadGame();
    }
}
