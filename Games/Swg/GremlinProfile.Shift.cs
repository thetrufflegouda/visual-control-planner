using System.Xml.Linq;

namespace VisualControlsBinder.Games;

public enum GremlinShiftBehavior { Momentary, Toggle }

internal sealed record GremlinProfileShift(Guid Device, int Button, string BaseMode, string ChildMode, GremlinShiftBehavior Behavior);

public sealed partial class GremlinProfile
{
    private const string ShiftActionLabel = "Visual Control Planner Shift";
    private const string ShiftModeMarker = "vcp-shift";
    private bool shiftChanged;
    internal IReadOnlyList<GremlinProfileShift> ActiveShifts { get; private set; }

    public IReadOnlyList<string> ShiftModes
    {
        get
        {
            var profile = template.Root!;
            return ModeElements(profile).Where(IsMarkedShiftMode).Select(mode => mode.Value)
                .Concat(ActiveShifts.Select(shift => shift.ChildMode))
                .Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    public string ConfigureShift(Guid deviceId, int button, string baseMode, GremlinShiftBehavior behavior)
    {
        if (deviceId == Guid.Empty || button < 1 || !Devices.Any(device => device.Id == deviceId))
            throw new InvalidOperationException("Choose a connected joystick button for the Gremlin shift.");
        if (!Modes.Contains(baseMode) || ShiftModes.Contains(baseMode))
            throw new InvalidOperationException("Choose the base Gremlin mode for the shift button.");
        if (behavior is not (GremlinShiftBehavior.Momentary or GremlinShiftBehavior.Toggle))
            throw new ArgumentOutOfRangeException(nameof(behavior));

        var document = new XDocument(template);
        var profile = document.Root!;
        var library = ActionLibrary(profile);
        var previous = FindOwnedShifts(profile, library).Where(shift => shift.BaseMode == baseMode).ToArray();
        string child = previous.Select(shift => shift.ChildMode).FirstOrDefault()
            ?? ChildModes(profile, baseMode).FirstOrDefault(IsMarkedShiftMode)?.Value
            ?? AddShiftMode(profile, baseMode);
        foreach (var shift in previous)
            RemoveModifier(profile, library, shift);

        FindInput(profile, deviceId, "button", button, baseMode)?.Remove();
        FindInput(profile, deviceId, "button", button, child)?.Remove();
        AddModeChange(profile, library, deviceId, button, baseMode, child,
            behavior == GremlinShiftBehavior.Momentary ? "Temporary" : "Switch");
        // Temporary receives the release through its original press callback. The child inherits that mapping.
        if (behavior == GremlinShiftBehavior.Toggle)
            AddModeChange(profile, library, deviceId, button, child, baseMode, "Switch");

        SetShiftDocument(document, library);
        return child;
    }

    public bool ClearShift(string baseMode)
    {
        var document = new XDocument(template);
        var profile = document.Root!;
        var library = ActionLibrary(profile);
        var shifts = FindOwnedShifts(profile, library).Where(shift => shift.BaseMode == baseMode).ToArray();
        if (shifts.Length == 0)
            return false;

        foreach (var shift in shifts)
            RemoveModifier(profile, library, shift);
        SetShiftDocument(document, library);
        return true;
    }

    private void SetShiftDocument(XDocument document, Dictionary<Guid, XElement> library)
    {
        var profile = document.Root!;
        template = document;
        Modes = ReadModes(profile);
        ActiveShifts = FindOwnedShifts(profile, library).Distinct().ToArray();
        mappings = ReadMappings(profile, library);
        routedOutputs = ReadRoutedOutputs(profile, library);
        shiftChanged = true;
    }

    private static string[] ReadModes(XElement profile) => ModeElements(profile).Select(mode => mode.Value).ToArray();

    private static IEnumerable<XElement> ModeElements(XElement profile) =>
        profile.Element("modes")?.Elements("mode") ?? [];

    internal IEnumerable<string> ModeChain(string mode)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var modes = ModeElements(template.Root!);
        for (string? current = mode; current is not null && visited.Add(current);
             current = (string?)modes.FirstOrDefault(element => element.Value == current)?.Attribute("parent"))
            yield return current;
    }

    internal string? ShiftModeForBase(string baseMode) => ActiveShifts.FirstOrDefault(shift => shift.BaseMode == baseMode)?.ChildMode
        ?? ChildModes(template.Root!, baseMode).FirstOrDefault(IsMarkedShiftMode)?.Value;

    private static IEnumerable<XElement> ChildModes(XElement profile, string baseMode) =>
        ModeElements(profile).Where(mode => (string?)mode.Attribute("parent") == baseMode);

    private static bool IsMarkedShiftMode(XElement mode) =>
        mode.Attribute("parent") is not null && (string?)mode.Attribute(ShiftModeMarker) == "true";

    private static string AddShiftMode(XElement profile, string baseMode)
    {
        XElement modes = profile.Element("modes")!;
        var names = modes.Elements("mode").Select(mode => mode.Value).ToHashSet(StringComparer.Ordinal);
        string stem = $"{baseMode} Shift (VCP)";
        string name = stem;
        for (int suffix = 2; names.Contains(name); suffix++)
            name = $"{stem} {suffix}";
        modes.Add(new XElement("mode", new XAttribute("parent", baseMode),
            new XAttribute(ShiftModeMarker, "true"), name));
        return name;
    }

    private static void AddModeChange(XElement profile, Dictionary<Guid, XElement> library,
        Guid deviceId, int button, string mode, string target, string changeType)
    {
        var action = new XElement("action", new XAttribute("type", "change-mode"),
            Property("string", "change-type", changeType),
            new XElement("target-mode", Property("string", "name", target)),
            Property("string", "action-label", ShiftActionLabel),
            Property("activation-mode", "activation-mode", "press"));
        var root = CreateRootInput(profile, library, deviceId, "button", button, mode);
        root.Element("actions")!.Add(new XElement("action-id", AddAction(profile, library, action)));
    }

    private static IEnumerable<GremlinProfileShift> FindOwnedShifts(XElement profile, Dictionary<Guid, XElement> library)
    {
        var modes = ModeElements(profile).ToArray();
        foreach (var input in profile.Element("inputs")!.Elements("input"))
        {
            if (Value(input, "input-type") != "button" ||
                !Guid.TryParse(Value(input, "device-id"), out Guid device) ||
                !int.TryParse(Value(input, "input-id"), out int button))
                continue;

            string baseMode = Value(input, "mode");
            foreach (var (_, root) in RouteRoots(input, library))
            foreach (var reference in root.Element("actions")?.Elements("action-id") ?? [])
            {
                if (Resolve(reference, library) is { } action &&
                    ShiftTarget(action) is { } target &&
                    modes.Any(mode => mode.Value == target && (string?)mode.Attribute("parent") == baseMode))
                    yield return new GremlinProfileShift(device, button, baseMode, target, PropertyValue(action, "change-type") == "Temporary"
                        ? GremlinShiftBehavior.Momentary : GremlinShiftBehavior.Toggle);
            }
        }
    }

    private static string? ShiftTarget(XElement action) =>
        (string?)action.Attribute("type") == "change-mode" &&
        PropertyValue(action, "action-label") == ShiftActionLabel &&
        PropertyValue(action, "change-type") is "Temporary" or "Switch" &&
        action.Element("target-mode") is { } target
            ? PropertyValue(target, "name") : null;

    private static void RemoveModifier(XElement profile, Dictionary<Guid, XElement> library, GremlinProfileShift shift)
    {
        // Gremlin ignores the marker. Active action labels also identify its modes after Gremlin saves a profile.
        ChildModes(profile, shift.BaseMode).First(mode => mode.Value == shift.ChildMode)
            .SetAttributeValue(ShiftModeMarker, "true");
        RemoveModeChange(shift.BaseMode, shift.ChildMode);
        RemoveModeChange(shift.ChildMode, shift.BaseMode);

        void RemoveModeChange(string source, string target)
        {
            var input = FindInput(profile, shift.Device, "button", shift.Button, source);
            if (input is null)
                return;
            foreach (var (reference, root) in RouteRoots(input, library).ToArray())
            {
                var references = root.Element("actions")?.Elements("action-id").ToArray() ?? [];
                var removed = references.Where(child => Resolve(child, library) is { } action &&
                    ShiftTarget(action) == target).ToArray();
                if (removed.Length == 0)
                    continue;

                if (references.Length == removed.Length)
                    reference.Parent!.Remove();
                else
                {
                    var copy = new XElement(root);
                    var removedIds = removed.Select(child => child.Value).ToHashSet(StringComparer.Ordinal);
                    copy.Element("actions")!.Elements("action-id").Where(child => removedIds.Contains(child.Value)).Remove();
                    reference.Value = AddAction(profile, library, copy).ToString();
                }
                RemoveUnusedAction(profile, library, Guid.Parse((string)root.Attribute("id")!));
                foreach (var actionReference in removed)
                    RemoveUnusedAction(profile, library, Guid.Parse(actionReference.Value));
            }
            if (!input.Elements("action-configuration").Any())
                input.Remove();
        }
    }

    private static void RemoveUnusedAction(XElement profile, Dictionary<Guid, XElement> library, Guid id)
    {
        if (profile.Descendants().Any(element =>
            element.Name.LocalName is "root-action" or "action-id" &&
            Guid.TryParse(element.Value, out var reference) && reference == id))
            return;
        if (library.Remove(id, out var action))
            action.Remove();
    }

}
