using System.IO;
using System.Text;
using VisualControlsBinder.Games;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{

    private string? ExportSquadrons(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        var path = SquadronsProfile(game) ?? throw new InvalidOperationException("Choose the Squadrons settings file first.");
        if (!Path.GetFileName(path).Equals(SquadronsGame.ProfileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Choose the game's {SquadronsGame.ProfileName} settings file to write native bindings.");
        var profile = Games.SquadronsProfile.Load(path);
        var preview = new StringBuilder();
        var slots = new HashSet<int>();
        var usedDevices = new Dictionary<string, Guid>();
        foreach (var group in edits.GroupBy(edit => edit.Address.Device))
        {
            var hardware = GetLayout(group.Key);
            var device = ConnectedDevice(hardware) ?? throw new InvalidOperationException($"Connect {hardware.Name} first.");
            if (SquadSlot(profile, device) is not { } slot)
                return null;
            if (!slots.Add(slot))
                throw new InvalidOperationException("Two controllers selected the same Squadrons joystick slot.");
            usedDevices.Add(group.Key, device.Id);
            preview.AppendLine($"{hardware.Name} · Squadrons joystick {slot + 1}");
            foreach (var edit in group)
            {
                var signal = edit.RemoveInput is { } token
                    ? InputSignal.Parse(token) ?? throw new InvalidDataException("The Squadrons binding to remove has an invalid input.")
                    : RequireInput(hardware, edit.Address).Signal;
                bool applied = profile.Apply(edit.Action, slot, signal.Token, edit.RemoveInput is not null);
                preview.AppendLine($"  {(edit.RemoveInput is null ? "Assign" : "Remove")} · {edit.Action.Name} · {signal}{(applied ? "" : " · already matches")}");
            }
            preview.AppendLine();
        }
        preview.AppendLine(path).AppendLine("VCP writes the selected settings file and saves a backup beside it.");
        if (!ReviewWindow.Confirm(this, "Squadrons binding preview", preview.ToString(), "Update settings"))
            return null;
        RequireLivePlanDevices(edits, game);
        if (usedDevices.Any(entry => ConnectedDevice(GetLayout(entry.Key))?.Id != entry.Value))
            throw new InvalidOperationException("A controller changed during preview. Preview the Squadrons bindings again.");
        profile.SaveChanges();
        return "Updated the Squadrons control settings.";
    }

    private int? SquadSlot(SquadronsProfile profile, JoystickDevice device)
    {
        if (_connected.Count(other => other.Name.Equals(device.Name, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new InvalidOperationException($"Squadrons reports the same product name for two connected {device.Name} controllers.");
        if (profile.FindDeviceSlot(device.Name) is { } known)
            return known;
        var matches = profile.Devices.Where(slot => Games.SquadronsProfile.DeviceNameMatches(slot.Value, device.Name))
            .Select(slot => new SquadronsDeviceChoice(slot.Key, slot.Value)).ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException($"Register {device.Name} in Squadrons’ Controls first so it has a native joystick slot.");
        return ReviewWindow.Pick(this, $"Choose the in-game joystick slot for {device.Name}", matches)?.Slot;
    }

    private sealed record SquadronsDeviceChoice(int Slot, string Name)
    {
        public override string ToString() => $"Joystick {Slot + 1} · {Name}";
    }
}
