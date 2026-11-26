using System.Globalization;

namespace VisualControlsBinder.Games;

internal sealed class WarThunderVJoyBindings
{
    private readonly WarThunderProfile _profile;
    private readonly WarThunderDevice _device;
    private readonly HashSet<GremlinOutput> _outputs;

    public WarThunderVJoyBindings(WarThunderProfile profile, IReadOnlyList<GremlinOutput> capabilities)
    {
        _profile = profile;
        _device = profile.FindDevice(0x1234, 0xbead) is { Connected: true } device
            ? device : throw new InvalidOperationException("Choose War Thunder controls with one saved, connected vJoy device.");
        var matches = capabilities.Distinct().GroupBy(output => output.DeviceId)
            .Where(outputs => outputs.Count(output => output.Kind == "button") == _device.ButtonsCount &&
                outputs.Count(output => output.Kind == "axis") == _device.AxesCount).Take(2).ToArray();
        if (matches is not [var match])
            throw new InvalidOperationException("War Thunder's saved vJoy device must match exactly one connected vJoy by button and axis count.");

        DeviceId = match.Key;
        _outputs = match.ToHashSet();
    }

    public int DeviceId { get; }

    public IReadOnlyDictionary<GremlinOutput, RoutedBindingRead> Read(IReadOnlyCollection<GremlinOutput> outputs) =>
        RoutedBindingRead.Read(outputs, Input, _profile.Bindings.Distinct());

    public string Input(GremlinOutput output)
    {
        int id = NativeId(output);
        return Token(output.Kind, id);
    }

    public bool Apply(GameAction action, GremlinOutput output, BindingActivation activation, string? removeInput)
    {
        bool axis = output.Kind == "axis";
        if (action.IsAxis != axis || action.Map != (axis ? "axes" : "hotkeys"))
            throw new InvalidOperationException($"{action.Name} needs a vJoy {(action.IsAxis ? "axis" : "button")}.");
        int id = NativeId(output);
        bool remove = removeInput is not null;
        if (remove && !string.Equals(removeInput, Token(output.Kind, id), StringComparison.Ordinal))
            throw new InvalidOperationException("The selected vJoy route changed. Reload this War Thunder control before removing the binding.");
        return axis ? _profile.ApplyAxis(action.Id, _device, output.InputId, remove)
            : _profile.ApplyButton(action.Id, id, activation == BindingActivation.Default ? BindingActivation.Tap : activation, remove);
    }

    public IReadOnlyList<NativeBindingWrite> ToWrites(string? copyPath = null) => _profile.ToWrites(copyPath);

    public void EnsureGameClosed() => _profile.EnsureGameClosed();

    private int NativeId(GremlinOutput output)
    {
        if (!_outputs.Contains(output))
            throw new InvalidOperationException($"Choose an input on vJoy {DeviceId}, the device identified by this War Thunder profile.");
        int id = output.Kind switch
        {
            "button" => _device.NativeButton(output.InputId),
            "axis" => _device.NativeDirectInputAxis(output.InputId),
            _ => throw new InvalidOperationException("Choose a vJoy button or axis for this War Thunder action.")
        };
        if (_profile.Devices.Any(other => other != _device && (output.Kind == "axis"
            ? IsInRange(id, other.AxesOffset, other.AxesCount)
            : IsInRange(id, other.ButtonsOffset, other.ButtonsCount))))
            throw new InvalidOperationException("The saved War Thunder device map gives this input to more than one joystick.");
        return id;
    }

    private static string Token(string kind, int id) =>
        $"{(kind == "axis" ? "axisId" : "joyButton")}:{id.ToString(CultureInfo.InvariantCulture)}";

    private static bool IsInRange(int id, int offset, int count) => offset >= 0 && id >= offset && id - offset < count;
}
