using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Games;

internal sealed class SwgVJoyBindings
{
    private readonly SwgNativeProfile _profile;
    private readonly int? _deviceId;

    public SwgVJoyBindings(SwgNativeProfile profile, IReadOnlyCollection<int> routedDeviceIds)
    {
        var devices = routedDeviceIds.Distinct().Take(2).ToArray();
        if (devices.Length > 1)
            throw new InvalidOperationException("SWG's saved keymap uses one joystick. Route this game's assignments to one vJoy device and select it in the SWG launcher.");
        if (devices is [< 1 or > 16])
            throw new ArgumentOutOfRangeException(nameof(routedDeviceIds), "Choose an available vJoy device for SWG.");
        _profile = profile;
        _deviceId = devices is [var deviceId] ? deviceId : null;
    }

    public IReadOnlyDictionary<GremlinOutput, RoutedBindingRead> Read(IReadOnlyCollection<GremlinOutput> outputs) =>
        RoutedBindingRead.Read(outputs, Input, _profile.Bindings.Select(binding =>
            new GameBinding(binding.Map, binding.Action, binding.Kind == "axis"
                ? InputSignal.Axis(binding.Index).Token : InputSignal.Button(binding.Index).Token)).Distinct());

    public string Input(GremlinOutput output) => RequireSignal(output).Token;

    public SwgNativeChange Apply(GameAction action, GremlinOutput output, string? removeInput)
    {
        var signal = RequireSignal(output);
        if (removeInput is not null && !removeInput.Equals(signal.Token, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected vJoy route changed. Reload this SWG control before removing the binding.");
        return _profile.Apply(action, signal.Kind, signal.Index, removeInput is not null);
    }

    public IReadOnlyList<NativeBindingWrite> ToWrites(string? copyFolder = null) => _profile.ToWrites(copyFolder);

    public void EnsureGameClosed() => _profile.EnsureGameClosed();

    private InputSignal RequireSignal(GremlinOutput output) => Signal(output)
        ?? throw new InvalidOperationException("Choose a button 1–32 or axis on the vJoy device selected for this SWG keymap.");

    private InputSignal? Signal(GremlinOutput output) => _deviceId == output.DeviceId ? (output.Kind, output.InputId) switch
    {
        ("button", >= 1 and <= 32) => InputSignal.Button(output.InputId),
        ("axis", >= 1 and <= 8) => InputSignal.Axis(output.InputId),
        _ => null
    } : null;
}
