using Microsoft.Win32;
using System.IO;
using System.Text;
using VisualControlsBinder.Games;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private static string MsfsBindingProfileKey(GameTarget game) => $"{game.Id}/native";

    private MsfsBindingProfile? UniqueMsfsProfile(GameTarget game, JoystickDevice device)
    {
        if (_connected.Count(item => item.Product == device.Product && item.Name == device.Name) > 1)
            return null;
        var candidates = _profiles.Msfs.Where(profile => profile.Matches(device)).ToArray();
        var chosen = _plan.SourceProfiles.GetValueOrDefault(MsfsBindingProfileKey(game));
        return candidates.FirstOrDefault(profile => profile.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase)) ?? Single(candidates);
    }

    private async Task ChooseMsfsBindingProfile(GameTarget game)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose a Microsoft Flight Simulator device profile",
            Filter = "Named control files|*.xml;*.inputprofile;inputprofile_*|All files|*.*",
            FilterIndex = 2,
            InitialDirectory = ProfileDialogDirectory(game)
        };
        if (picker.ShowDialog(this) != true)
            return;
        var plan = _plan;
        int request = _catalogRequest;
        string file = picker.FileName;
        try
        {
            await Task.Run(() => MsfsBindingProfile.Load(file));
        }
        catch (Exception error)
        {
            if (IsCurrentGameRequest(plan, game, request))
                Notify(error.Message);
            return;
        }
        if (!IsCurrentGameRequest(plan, game, request))
            return;
        plan.SourceProfiles[MsfsBindingProfileKey(game)] = file;
        await LoadGame();
    }

    private async Task<string?> ExportMsfs(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        var pending = new List<NativeBindingWrite>();
        var cloud = new List<NativeBindingWrite>();
        var devices = new Dictionary<string, Guid>();
        var preview = new StringBuilder();
        foreach (var group in edits.GroupBy(edit => edit.Address.Device))
        {
            var hardware = GetLayout(group.Key);
            var device = ConnectedDevice(hardware) ?? throw new InvalidOperationException($"Connect {hardware.Name} first.");
            if (_connected.Count(item => item.Product == device.Product && item.Name == device.Name) > 1)
                throw new InvalidOperationException($"MSFS reports the same model identity for two connected {device.Name} controllers.");
            var target = UniqueMsfsProfile(game, device);
            if (target is null)
            {
                var matches = _profiles.Msfs.Where(profile => profile.Matches(device)).Select(profile => profile.Path).ToArray();
                if (matches.Length == 0)
                    throw new InvalidOperationException($"Save a controls preset for {device.Name} in MSFS, then choose its device profile.");
                var chosen = ReviewWindow.Pick(this, $"Choose the MSFS profile for {device.Name}", matches);
                if (chosen is null)
                    return null;
                target = _profiles.Msfs.Single(profile => profile.Path == chosen);
            }
            var profile = MsfsBindingProfile.Load(target.Path);
            if (!profile.Matches(device))
                throw new InvalidOperationException($"The profile for {device.Name} changed. Reload it before exporting.");
            devices.Add(group.Key, device.Id);
            preview.AppendLine($"{hardware.Name}").AppendLine(profile.Path);
            bool changed = false;
            foreach (var edit in group)
            {
                var signal = edit.RemoveInput is { } token
                    ? InputSignal.Parse(token) ?? throw new InvalidDataException("The Flight Simulator binding to remove has an invalid input.")
                    : RequireInput(hardware, edit.Address).Signal;
                bool applied = profile.Apply(edit.Action, signal, edit.RemoveInput is not null);
                changed |= applied;
                preview.AppendLine($"  {(edit.RemoveInput is null ? "Assign" : "Remove")} · {edit.Action.Name} · {signal}{(applied ? "" : " · already matches")}");
            }
            if (changed && profile.IsCloudSave)
                cloud.Add(profile.ToCloudWrite());
            else if (changed)
                pending.Add(profile.ToWrite());
            preview.AppendLine();
        }
        if (pending.Count == 0 && cloud.Count == 0)
            return "The Flight Simulator device profiles already match the plan.";
        if (pending.Count > 0 && cloud.Count > 0)
            throw new InvalidOperationException("Export local profiles and Xbox cloud profiles in separate previews.");
        bool is2024 = game.Kind == GameKind.Msfs2024;
        if (cloud.Count > 0 && MsfsCloudBindingWrites.TryGetUnavailableReason(is2024) is { } reason)
            throw new InvalidOperationException(reason);
        preview.AppendLine(cloud.Count > 0 ? "VCP backs up the local Xbox save before updating the selected controls."
            : "Each changed file gets a backup in the same folder.");
        if (!ReviewWindow.Confirm(this, "Microsoft Flight Simulator binding preview", preview.ToString(), "Update profiles"))
            return null;
        RequireLivePlanDevices(edits, game);
        if (devices.Any(entry => ConnectedDevice(GetLayout(entry.Key))?.Id != entry.Value))
            throw new InvalidOperationException("A controller changed during preview. Preview the MSFS bindings again.");
        if (cloud.Count > 0)
        {
            Notify("Updating the selected Microsoft Flight Simulator controls…");
            var backup = await Task.Run(() => MsfsCloudBindingWrites.Commit(cloud, is2024));
            return $"Updated {cloud.Count} MSFS Xbox device profile(s). Recovery files are in {backup}.";
        }
        NativeBindingWrites.Commit(pending, MsfsBindingProfile.EnsureGameClosed);
        return $"Updated {pending.Count} Microsoft Flight Simulator device profile(s).";
    }
}
