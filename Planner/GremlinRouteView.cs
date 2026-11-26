using VisualControlsBinder.Games;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Planner;

internal sealed record GremlinRouteView(string? DeviceId, ControlAddress? Address, InputSignal? Signal,
    SwgTarget Target, bool Inherited, bool Staged, bool Reference, string? DeviceName, string? NativeMode = null);
