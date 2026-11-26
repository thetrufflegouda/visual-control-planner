using System.IO;
using System.Windows;
using Microsoft.Win32;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private sealed record DcsProfile(string? Path, IReadOnlyList<DcsSavedBinding> Bindings, bool Selected, string? Error = null);

    private readonly Dictionary<(DcsModule Module, string Slot, Guid? Instance, string? Selection), DcsProfile> _dcsProfiles = [];
    private DcsProfile? _selectedDcsProfile;

    private void RefreshDcsProfile()
    {
        if (DcsProfileText is null)
            return;
        bool active = SelectedGame?.Kind == GameKind.Dcs;
        bool direct = active && SelectedGame is { } game && OutputFor(game) == OutputMode.DirectToGame;
        DcsProfileRow.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _selectedDcsProfile = active && _aircraft is { } module && _hardware is { } hardware
            ? DcsProfileFor(module, hardware) : null;
        var profile = _selectedDcsProfile;
        ChooseDcsProfileButton.IsEnabled = active && _aircraft is not null && _hardware is not null;
        ChooseDcsProfileButton.Visibility = direct ? Visibility.Collapsed : Visibility.Visible;
        CopyDcsProfileButton.IsEnabled = profile?.Path is not null;
        DcsProfileText.Text = profile switch
        {
            { Error: { } message } => message,
            { Path: { } file } => $"{(profile.Selected ? "Selected profile" : "Active DCS profile")} · {file}",
            _ when _aircraft is null => "Choose an aircraft.",
            _ when _hardware is null => "Select a controller.",
            null => "Connect the controller to find its active DCS file, or choose a saved profile.",
            _ => "No saved DCS file for this aircraft and controller."
        };
        DcsProfileText.ToolTip = profile?.Path ?? (_aircraft is { } aircraft ? DcsGame.SavedJoystickDirectory(aircraft) : null);
        if (direct)
        {
            SourceButton.IsEnabled = ChooseDcsProfileButton.IsEnabled;
            SourceButton.Content = profile?.Path is { } path ? Path.GetFileName(path) : "Choose DCS profile…";
            SourceButton.ToolTip = DcsProfileText.ToolTip;
            NewProfileButton.IsEnabled = ChooseDcsProfileButton.IsEnabled &&
                (profile is { Path: not null, Error: null } || _hardware is { } layout && ConnectedDevice(layout) is not null);
        }
    }

    private DcsProfile? DcsProfileFor(DcsModule module, HardwareDefinition hardware)
    {
        var selection = _plan.GetDcsProfile(module.Id, hardware.Id);
        var instance = _plan.Devices.FirstOrDefault(slot => slot.Id == hardware.Id)?.InstanceId;
        if (selection is null && instance is null)
            return null;
        var key = (module, hardware.Id, instance, selection);
        if (_dcsProfiles.TryGetValue(key, out var known))
            return known;

        string? path = selection;
        try
        {
            if (path is null && instance is { } paired)
                path = DcsGame.FindSavedJoystickProfile(module, paired);
            return _dcsProfiles[key] = new DcsProfile(path, path is null ? [] : DcsDiffProfile.Load(path).Bindings,
                Selected: selection is not null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return _dcsProfiles[key] = new DcsProfile(path, [], Selected: selection is not null,
                $"DCS profile could not be read. {error.Message}");
        }
    }

    private void ChooseDcsDeviceProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedGame?.Kind != GameKind.Dcs || _aircraft is not { } module || _hardware is not { } hardware)
            return;
        const string browse = "Choose another DCS profile";
        const string active = "Use active DCS profile";
        if (_plan.GetDcsProfile(module.Id, hardware.Id) is not null)
        {
            var choice = ReviewWindow.Pick(this, "DCS controller profile", new[] { browse, active });
            if (choice is null)
                return;
            if (choice == active)
            {
                SelectDcsProfile(module, hardware, null);
                return;
            }
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Choose the {module.Name} profile for {hardware.Name}",
            Filter = "DCS controller profiles (*.diff.lua)|*.diff.lua",
            InitialDirectory = ExistingProfileDirectory([Path.GetDirectoryName(_selectedDcsProfile?.Path),
                DcsGame.SavedJoystickDirectory(module), module.SavedGamesDirectory])
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            DcsDiffProfile.Load(dialog.FileName);
            SelectDcsProfile(module, hardware, dialog.FileName);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Notify($"DCS profile could not be loaded. {error.Message}");
        }
    }

    private void SelectDcsProfile(DcsModule module, HardwareDefinition hardware, string? path)
    {
        _plan.SetDcsProfile(module.Id, hardware.Id, path);
        _dcsProfiles.Clear();
        UpdateControl(revealAssignment: true);
        Notify(path is null ? $"Using the active DCS profile for {hardware.Name}."
            : $"Loaded {Path.GetFileName(path)} for {hardware.Name}.");
    }

    private void CopyDcsProfilePath(object sender, RoutedEventArgs e)
    {
        if (_selectedDcsProfile?.Path is not { } path)
            return;
        try
        {
            Clipboard.SetText(path);
            Notify("Copied the DCS profile path.");
        }
        catch (System.Runtime.InteropServices.ExternalException error) { Notify(error.Message); }
    }

    private IEnumerable<NativeDiagramBinding> DcsSavedRows(DcsModule module, HardwareDefinition hardware, DcsProfile profile)
    {
        var catalog = _actions.ToLookup(action => (action.Name, action.IsAxis));
        var nativeIds = profile.Bindings.ToLookup(binding => (binding.ActionName, binding.IsAxis));
        foreach (var binding in profile.Bindings.Where(binding => binding.Change is "added" or "changed"))
        {
            var namedIds = nativeIds[(binding.ActionName, binding.IsAxis)].Select(item => item.ActionId).Distinct().Take(2);
            var choice = namedIds.Count() == 1 ? Single(catalog[(binding.ActionName, binding.IsAxis)]) : null;
            var action = choice ?? new GameAction(module.Id, binding.ActionId, "DCS saved bindings", "",
                binding.ActionName, binding.IsAxis);
            var signal = binding.Reformers.Count == 0 ? DcsJoystickInputs.Parse(binding.Key) : null;
            var input = string.Join(" + ", binding.Reformers.Append(binding.Key));
            yield return new NativeDiagramBinding(hardware.Id, signal?.Token, input, action);
        }
    }
}
