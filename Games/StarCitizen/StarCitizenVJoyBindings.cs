using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Games;

internal sealed class StarCitizenVJoyBindings(StarCitizenProfile profile, IReadOnlyCollection<int> routedDeviceIds)
{
    // One product GUID is shared by all vJoy devices. The game slot is read from the profile.
    internal static readonly Guid ProductId = new("BEAD1234-0000-0000-0000-504944564944");

    public IReadOnlyDictionary<GremlinOutput, RoutedBindingRead> Read(IReadOnlyCollection<GremlinOutput> outputs) =>
        RoutedBindingRead.Read(outputs, Input, profile.Bindings, normalizeInput: true);

    public string Input(GremlinOutput output) => ResolveInput(output, out var error) ?? throw new InvalidOperationException(error);

    public bool Apply(GameAction action, GremlinOutput output, BindingActivation activation, string? removeInput,
        string? nativeActivation)
    {
        if (action.IsAxis != (output.Kind == "axis"))
            throw new InvalidOperationException($"{action.Name} needs a vJoy {(action.IsAxis ? "axis" : "button")}.");

        string input = Input(output);
        if (removeInput is null)
            return profile.ApplyActivation(action.Map, action.Id, input, activation);
        if (!input.Equals(removeInput.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The vJoy input changed. Reload the Star Citizen profile before unassigning this binding.");
        return profile.RemoveActivationForDevice(action.Map, action.Id, input, ProductId, nativeActivation);
    }

    public NativeBindingWrite? ToWrite(string? copyPath = null) => profile.ToWrite(copyPath);

    private string? ResolveInput(GremlinOutput output, out string? error)
    {
        var signal = output.Kind switch
        {
            "button" when output.InputId is >= 1 and <= 128 => InputSignal.Button(output.InputId),
            "axis" when output.InputId is >= 1 and <= 8 => InputSignal.Axis(output.InputId),
            _ => null
        };
        if (signal is null || output.DeviceId is < 1 or > 16)
        {
            error = "Choose a valid vJoy button or axis for the Star Citizen action.";
            return null;
        }
        if (routedDeviceIds.Any(id => id != output.DeviceId))
        {
            error = "Use one vJoy device for this Star Citizen plan so VCP can match it to a joystick slot.";
            return null;
        }

        var registered = profile.Devices.Where(device => device.ProductGuid == ProductId).Take(2).ToArray();
        if (registered is not [var device] || !profile.HasUniqueJoystickSlot(device.Instance))
        {
            error = "Choose a Star Citizen Controls profile with one registered vJoy joystick slot.";
            return null;
        }
        error = null;
        return $"js{device.Instance}_{signal.Token}";
    }
}
