using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualControlsBinder.Hardware.Custom;

internal enum CustomPanelShape { Rectangle, RoundedRectangle }
internal enum CustomPartKind
{
    Button,
    MomentarySwitch,
    Encoder,
    Axis,
    LatchingButton,
    OnOffSwitch,
    OnOnSwitch,
    ThreePositionOnOffOn,
    ThreePositionTopMomentaryOff,
    ThreePositionBottomMomentaryOff,
    ThreePositionBothMomentaryOff,
    ThreePositionOnOnOn,
    ThreePositionTopMomentaryOn,
    ThreePositionBottomMomentaryOn,
    ThreePositionBothMomentaryOn,
    Thumbstick,
    FourWayHat,
    Rocker,
    RotaryAxis,
    FivePositionSelector
}

internal sealed record CustomPart(
    string Id, string Name, CustomPartKind Kind, double X, double Y, double Width, double Height, int QuarterTurns = 0)
{
    public static string Label(CustomPartKind kind) => kind switch
    {
        CustomPartKind.MomentarySwitch => "Two-way momentary switch",
        CustomPartKind.Encoder => "Encoder",
        CustomPartKind.Axis => "Slider axis",
        CustomPartKind.LatchingButton => "Latching push button",
        CustomPartKind.Thumbstick => "Analog thumbstick + press",
        CustomPartKind.FourWayHat => "Four-way hat + press",
        CustomPartKind.Rocker => "On–off rocker",
        CustomPartKind.RotaryAxis => "Rotary axis",
        CustomPartKind.FivePositionSelector => "Five-position rotary selector",
        _ when Contacts(kind) is { } contacts => string.Join("–",
            new[] { contacts.Up, contacts.Center, contacts.Down }.Where(state => state is not null)) + " toggle",
        _ => "Momentary button"
    };

    public bool CanRotate => Kind is CustomPartKind.Axis or CustomPartKind.Rocker
        or CustomPartKind.Encoder or CustomPartKind.RotaryAxis or CustomPartKind.FivePositionSelector
        || Contacts(Kind) is not null;

    public static (double Width, double Height) DefaultSize(CustomPartKind kind) => kind switch
    {
        CustomPartKind.Axis => (48, 88),
        CustomPartKind.Rocker => (48, 70),
        CustomPartKind.Thumbstick or CustomPartKind.FivePositionSelector => (74, 74),
        _ when Contacts(kind)?.Center is not null => (56, 82),
        _ => (64, 64)
    };

    internal static SwitchContacts? Contacts(CustomPartKind kind) => kind switch
    {
        CustomPartKind.MomentarySwitch => new("(ON)", null, "(ON)"),
        CustomPartKind.OnOffSwitch => new("ON", null, "OFF"),
        CustomPartKind.OnOnSwitch => new("ON", null, "ON"),
        CustomPartKind.ThreePositionOnOffOn => new("ON", "OFF", "ON"),
        CustomPartKind.ThreePositionTopMomentaryOff => new("(ON)", "OFF", "ON"),
        CustomPartKind.ThreePositionBottomMomentaryOff => new("ON", "OFF", "(ON)"),
        CustomPartKind.ThreePositionBothMomentaryOff => new("(ON)", "OFF", "(ON)"),
        CustomPartKind.ThreePositionOnOnOn => new("ON", "ON", "ON"),
        CustomPartKind.ThreePositionTopMomentaryOn => new("(ON)", "ON", "ON"),
        CustomPartKind.ThreePositionBottomMomentaryOn => new("ON", "ON", "(ON)"),
        CustomPartKind.ThreePositionBothMomentaryOn => new("(ON)", "ON", "(ON)"),
        _ => null
    };

    public IEnumerable<HardwareControl> Inputs()
    {
        return Kind switch
        {
            CustomPartKind.MomentarySwitch => [Input("up", "up"), Input("down", "down")],
            CustomPartKind.Encoder => [Input("cw", "clockwise"), Input("ccw", "counterclockwise"), Input("press", "press")],
            CustomPartKind.Axis or CustomPartKind.RotaryAxis => [new HardwareControl(Id, Name, "Axis", null, null)],
            CustomPartKind.OnOffSwitch or CustomPartKind.Rocker => [Input("up", "on"), Input("down", "off")],
            CustomPartKind.OnOnSwitch => [Input("up", "up"), Input("down", "down")],
            CustomPartKind.Thumbstick => [Input("x", "horizontal axis", "Axis"), Input("y", "vertical axis", "Axis"), Input("press", "press")],
            CustomPartKind.FourWayHat => [Input("up", "up"), Input("right", "right"), Input("down", "down"),
                Input("left", "left"), Input("press", "press")],
            CustomPartKind.FivePositionSelector => Enumerable.Range(1, 5).Select(position => Input($"position-{position}", $"position {position}")),
            _ when Contacts(Kind)?.Center is not null => [Input("up", "up"), Input("center", "center"), Input("down", "down")],
            _ => [new HardwareControl(Id, Name, "Button", null, null)]
        };
    }

    public CustomPart At(double x, double y) => this with
    {
        X = Math.Clamp(x, Width / 2, 1 - Width / 2),
        Y = Math.Clamp(y, Height / 2, 1 - Height / 2)
    };

    private HardwareControl Input(string suffix, string label, string kind = "Button") =>
        new($"{Id}-{suffix}", $"{Name} {label}", kind, null, Id);

    internal sealed record SwitchContacts(string Up, string? Center, string Down);
}

internal sealed record CustomController(
    string Id, string Name, CustomPanelShape Shape, double Width, double Height,
    Guid? DeviceId, Guid? ProductId, IReadOnlyList<CustomPart> Parts)
{
    public HardwareDefinition ToHardwareDefinition()
    {
        var product = ProductId is Guid id ? BitConverter.ToUInt32(id.ToByteArray()) : 0;
        var controls = Parts.SelectMany(part => part.Inputs()).ToArray();
        var hotspots = Parts.Select(part => new HardwareHotspot(
            part.Inputs().First().Id, part.X, part.Y, part.Width, part.Height)).ToArray();
        return new HardwareDefinition(Id, Name, (int)(product & 0xffff), (int)(product >> 16),
            [new HardwareView("panel", "Panel", "", hotspots)], controls);
    }
}

internal static class CustomControllerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private static readonly string CatalogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VisualControlsBinder", "custom-controllers.json");

    public static IReadOnlyList<CustomController> Load()
    {
        if (!File.Exists(CatalogPath))
            return [];

        using var file = new FileStream(CatalogPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<CustomController[]>(file, JsonOptions)
            ?? throw new InvalidDataException("The custom controller catalog is empty.");
    }

    public static void Save(CustomController controller)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        // Hold a per-catalog lock across the read/merge/write, including across app instances.
        using var writeLock = new FileStream(CatalogPath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var controllers = Load().ToList();
        var index = controllers.FindIndex(existing => existing.Id == controller.Id);
        if (index < 0)
            controllers.Add(controller);
        else
            controllers[index] = controller;

        var pending = CatalogPath + ".pending";
        using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, controllers, JsonOptions);
            file.Flush(flushToDisk: true);
        }

        File.Move(pending, CatalogPath, overwrite: true);
    }
}
