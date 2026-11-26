using System.Buffers.Binary;
using System.Globalization;
using SharpGen.Runtime;
using Vortice.DirectInput;

namespace VisualControlsBinder.Inputs;

internal sealed record JoystickDevice(Guid Id, Guid Product, string Name)
{
    public string? NativeName { get; init; }
    public bool Matches(int vid, int pid)
    {
        Span<byte> bytes = stackalloc byte[16];
        Product.TryWriteBytes(bytes);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return (vid == 0 || (id & 0xffff) == vid) && (pid == 0 || (id >> 16) == pid);
    }
}

internal sealed record InputSignal(string Token, string Kind, int Index, string? Direction = null)
{
    private static readonly string[] AxisTokens = ["x", "y", "z", "rotx", "roty", "rotz", "slider1", "slider2"];

    public static InputSignal Axis(int index) => new(AxisTokens[index - 1], "axis", index);
    public static InputSignal Button(int index) => new($"button{index}", "button", index);
    public static InputSignal Hat(int index, string direction) => new($"hat{index}_{direction}", "hat", index, direction);

    public static InputSignal? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        token = token.Trim().ToLowerInvariant();
        var axis = Array.IndexOf(AxisTokens, token);
        if (axis >= 0)
            return Axis(axis + 1);
        if (token.StartsWith("button", StringComparison.Ordinal) &&
            int.TryParse(token.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var button) &&
            button is >= 1 and <= 128)
            return Button(button);
        var parts = token.Split('_');
        if (parts.Length == 2 && parts[0].StartsWith("hat", StringComparison.Ordinal) &&
            int.TryParse(parts[0].AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var hat) &&
            hat is >= 1 and <= 4 &&
            parts[1] is "up" or "right" or "down" or "left")
            return Hat(hat, parts[1]);
        return null;
    }

    public override string ToString() => Kind switch
    {
        "axis" => $"Axis {Token.ToUpperInvariant()}",
        "hat" => $"POV {Index} {Direction}",
        _ => $"Button {Index}"
    };
}

internal sealed class DirectInputReader : IDisposable
{
    private const int BufferedEvents = 256;
    private readonly IDirectInput8 _input = DInput.DirectInput8Create();

    public IReadOnlyList<JoystickDevice> Enumerate() => _input
        .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
        .Select(device => new JoystickDevice(device.InstanceGuid, device.ProductGuid, device.ProductName.Trim()) { NativeName = device.ProductName })
        .ToArray();

    public JoystickCapture Open(JoystickDevice device, IntPtr window, bool axes)
    {
        var controller = _input.CreateDevice(device.Id);
        try
        {
            controller.SetCooperativeLevel(window, CooperativeLevel.NonExclusive | CooperativeLevel.Foreground).CheckError();
            controller.SetDataFormat<RawJoystickState>().CheckError();
            var bufferSize = EnableBuffer(controller);
            controller.Acquire().CheckError();
            return new JoystickCapture(controller, axes, bufferSize);
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    private static int EnableBuffer(IDirectInputDevice8 controller)
    {
        try
        {
            controller.Properties.BufferSize = BufferedEvents;
            return BufferedEvents;
        }
        catch (SharpGenException)
        {
            // Fall back to state polling when the driver does not support buffered events.
            return 0;
        }
    }

    public void Dispose() => _input.Dispose();
}

internal sealed class JoystickCapture : IDisposable
{
    // Vortice's public state reader omits the HRESULT. Check it before using the data.
    private const int GetDeviceStateSlot = 9;
    private const int GetDeviceDataSlot = 10;
    private readonly IDirectInputDevice8 _device;
    private readonly bool _axes;
    private readonly BufferedInputEvent[] _events;
    private readonly DigitalInputTracker _digital;
    private readonly AxisInputTracker _analog = new();

    public JoystickCapture(IDirectInputDevice8 device, bool axes, int bufferSize)
    {
        _device = device;
        _axes = axes;
        _events = new BufferedInputEvent[bufferSize];
        var capabilities = device.Capabilities;
        _digital = new DigitalInputTracker(capabilities.ButtonCount, capabilities.PovCount);
        if (!TryRead(out var initial, out _))
            throw new InvalidOperationException("Could not read the controller. Focus this window and try again.");
        Reset(initial);
    }

    public bool TryReadChanges(out IReadOnlyList<InputSignal> changes) => TryReadChanges(_axes, out changes);

    public bool TryResetBaseline(IReadOnlyList<InputSignal> waitForRelease)
    {
        if (!TryRead(out var current, out _) || waitForRelease.Any(signal => _digital.IsActive(current, signal)))
            return false;
        Reset(current);
        return true;
    }

    public bool TryResetAxisBaseline()
    {
        if (!TryRead(out var current, out bool reacquired))
            return false;
        if (reacquired)
            Reset(current);
        else
            _analog.Reset(current);
        return true;
    }

    public bool TryReadChanges(bool axesOnly, out IReadOnlyList<InputSignal> changes)
    {
        changes = [];
        if (!TryRead(out var current, out bool reacquired))
            return false;
        if (reacquired)
        {
            Reset(current);
            return true;
        }

        // Follow button state even during an axis step, so old button pulses do not spill into the next step.
        changes = _digital.Read(current, ReadBuffered(), report: !axesOnly);
        if (axesOnly)
            changes = _analog.Read(current);
        else
            _analog.Reset(current);
        return true;
    }

    private unsafe void Reset(RawJoystickState current)
    {
        if (_events.Length != 0)
        {
            int discardAll = -1;
            GetDeviceData(null, &discardAll);
        }
        _digital.Reset(current);
        _analog.Reset(current);
    }

    private unsafe ReadOnlySpan<BufferedInputEvent> ReadBuffered()
    {
        if (_events.Length == 0)
            return [];
        int count = _events.Length;
        fixed (BufferedInputEvent* data = _events)
            if (GetDeviceData(data, &count) < 0 || (uint)count > (uint)_events.Length)
                return [];
        return _events.AsSpan(0, count);
    }

    private unsafe int GetDeviceData(BufferedInputEvent* data, int* count)
    {
        nint device = _device.NativePointer;
        var vtable = *(nint**)device;
        var read = (delegate* unmanaged[Stdcall]<nint, int, BufferedInputEvent*, int*, int, int>)vtable[GetDeviceDataSlot];
        return read(device, sizeof(BufferedInputEvent), data, count, 0);
    }

    private bool TryRead(out RawJoystickState state, out bool reacquired)
    {
        reacquired = false;
        if (_device.Poll().Success && ReadDeviceState(out state))
            return true;
        if (_device.Acquire().Failure || _device.Poll().Failure)
        {
            state = default;
            return false;
        }
        reacquired = true;
        return ReadDeviceState(out state);
    }

    private unsafe bool ReadDeviceState(out RawJoystickState state)
    {
        state = default;
        nint device = _device.NativePointer;
        var vtable = *(nint**)device;
        var getDeviceState = (delegate* unmanaged[Stdcall]<nint, int, RawJoystickState*, int>)vtable[GetDeviceStateSlot];
        fixed (RawJoystickState* pointer = &state)
            return getDeviceState(device, sizeof(RawJoystickState), pointer) >= 0;
    }

    public void Dispose()
    {
        _device.Unacquire();
        _device.Dispose();
    }
}

// Native DIDEVICEOBJECTDATA. Vortice's corresponding type is internal.
internal struct BufferedInputEvent
{
    public int Offset;
    public int Data;
    public int Timestamp;
    public int Sequence;
    public nuint ApplicationData;
}

internal sealed class DigitalInputTracker(int buttonCount, int povCount)
{
    // Offsets in the DIJOYSTATE2 data format used for both the native snapshot and buffered updates.
    private const int PovOffset = 32;
    private const int ButtonOffset = 48;
    private const int MaxButtons = 128;
    private const int MaxPovs = 4;
    private static readonly string[] HatDirections = ["up", "right", "down", "left"];
    private readonly int _buttonCount = Math.Clamp(buttonCount, 0, MaxButtons);
    private readonly int _povCount = Math.Clamp(povCount, 0, MaxPovs);
    private RawJoystickState _previous;

    public void Reset(RawJoystickState state) => _previous = state;

    public unsafe IReadOnlyList<InputSignal> Read(RawJoystickState current, ReadOnlySpan<BufferedInputEvent> events, bool report = true)
    {
        List<InputSignal>? changes = report ? [] : null;
        var next = _previous;
        Span<bool> buttonEvents = stackalloc bool[MaxButtons];
        Span<bool> povEvents = stackalloc bool[MaxPovs];
        buttonEvents.Clear();
        povEvents.Clear();

        // Preserve the order of short pulses and remember their last reported state even if the snapshot is older.
        foreach (var update in events)
        {
            int offset = update.Offset;
            if (offset >= ButtonOffset && offset < ButtonOffset + MaxButtons)
            {
                int button = offset - ButtonOffset;
                byte value = (byte)update.Data;
                AddButton(changes, button, next.Buttons[button], value);
                next.Buttons[button] = value;
                buttonEvents[button] = true;
            }
            else if (offset >= PovOffset && offset < ButtonOffset && (offset - PovOffset) % sizeof(int) == 0)
            {
                int hat = (offset - PovOffset) / sizeof(int);
                int value = update.Data;
                AddPov(changes, hat, next.PointOfViewControllers[hat], value);
                next.PointOfViewControllers[hat] = value;
                povEvents[hat] = true;
            }
        }

        for (int i = 0; i < _buttonCount; i++)
        {
            if (buttonEvents[i]) continue;
            AddButton(changes, i, next.Buttons[i], current.Buttons[i]);
            next.Buttons[i] = current.Buttons[i];
        }
        for (int i = 0; i < _povCount; i++)
        {
            if (povEvents[i]) continue;
            AddPov(changes, i, next.PointOfViewControllers[i], current.PointOfViewControllers[i]);
            next.PointOfViewControllers[i] = current.PointOfViewControllers[i];
        }
        _previous = next;
        return changes is null ? [] : changes;
    }

    public unsafe bool IsActive(RawJoystickState current, InputSignal signal) => signal.Kind switch
    {
        "button" when signal.Index is > 0 and <= MaxButtons => (current.Buttons[signal.Index - 1] & 0x80) != 0,
        "hat" when signal.Index is > 0 and <= MaxPovs =>
            (PovDirections(current.PointOfViewControllers[signal.Index - 1]) &
                (1 << Array.IndexOf(HatDirections, signal.Direction))) != 0,
        _ => false
    };

    private static void AddButton(List<InputSignal>? changes, int index, byte previous, byte current)
    {
        if (changes is not null && (previous & 0x80) == 0 && (current & 0x80) != 0)
            AddUnique(changes, InputSignal.Button(index + 1));
    }

    private static void AddPov(List<InputSignal>? changes, int index, int previous, int current)
    {
        if (changes is null || previous == current)
            return;
        int directions = PovDirections(current) & ~PovDirections(previous);
        for (int direction = 0; direction < HatDirections.Length; direction++)
            if ((directions & (1 << direction)) != 0)
                AddUnique(changes, InputSignal.Hat(index + 1, HatDirections[direction]));
    }

    private static void AddUnique(List<InputSignal> changes, InputSignal signal)
    {
        if (!changes.Contains(signal)) changes.Add(signal);
    }

    private static int PovDirections(int value) => (value & 0xffff) switch
    {
        0 => 0b0001,
        4500 => 0b0011,
        9000 => 0b0010,
        13500 => 0b0110,
        18000 => 0b0100,
        22500 => 0b1100,
        27000 => 0b1000,
        31500 => 0b1001,
        _ => 0
    };
}

internal sealed class AxisInputTracker
{
    private const int AxisMovementThreshold = 6000;
    private const int AxisReleaseThreshold = AxisMovementThreshold / 2;
    private RawJoystickState _baseline;
    private int _latched;

    public void Reset(RawJoystickState state)
    {
        _baseline = state;
        _latched = 0;
    }

    public unsafe IReadOnlyList<InputSignal> Read(RawJoystickState current)
    {
        var baseline = _baseline;
        Span<int> currentAxes = [current.X, current.Y, current.Z, current.RotationX, current.RotationY,
            current.RotationZ, current.Sliders[0], current.Sliders[1]];
        Span<int> baselineAxes = [baseline.X, baseline.Y, baseline.Z, baseline.RotationX, baseline.RotationY,
            baseline.RotationZ, baseline.Sliders[0], baseline.Sliders[1]];
        var axes = new List<(int Index, long Movement)>();
        for (var index = 0; index < currentAxes.Length; index++)
        {
            long movement = Math.Abs((long)currentAxes[index] - baselineAxes[index]);
            int bit = 1 << index;
            if ((_latched & bit) != 0)
            {
                if (movement <= AxisReleaseThreshold) _latched &= ~bit;
                continue;
            }
            if (movement >= AxisMovementThreshold)
                axes.Add((index, movement));
        }
        if (axes.Count == 0)
            return [];

        var peak = axes.Max(axis => axis.Movement);
        var changes = new List<InputSignal>();
        foreach (var axis in axes.Where(axis => axis.Movement * 2 >= peak).OrderByDescending(axis => axis.Movement))
        {
            _latched |= 1 << axis.Index;
            changes.Add(InputSignal.Axis(axis.Index + 1));
        }
        return changes;
    }
}
