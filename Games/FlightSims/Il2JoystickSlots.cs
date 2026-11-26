using System.Globalization;
using System.IO;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Games;

internal sealed record Il2JoystickSlot(int Number, string InstancePrefix, string Name);

internal sealed class Il2JoystickSlots(IReadOnlyList<Il2JoystickSlot> slots)
{
    private static readonly char[] AxisLetters = ['x', 'y', 'z', 'w', 's', 't', 'p', 'q'];
    private static readonly string[] Directions = ["up", "right", "down", "left"];

    internal static Il2JoystickSlots Load(string gameFolder)
    {
        var path = Path.Combine(gameFolder, "data", "input", "devices.txt");
        if (!File.Exists(path))
            return new([]);

        var slots = new List<Il2JoystickSlot>();
        foreach (string line in File.ReadLines(path))
        {
            string[] fields = line.Trim().TrimEnd('|').Split(',', 3, StringSplitOptions.TrimEntries);
            if (fields.Length != 3 || !ReadNumber(fields[0], out int slot))
                continue;
            string instance = Uri.UnescapeDataString(fields[1]).Trim('"');
            string name = Uri.UnescapeDataString(fields[2]).Trim();
            if (instance.Length >= 18 && name.Length > 0)
                slots.Add(new(slot, instance[..18], name));
        }
        return new(slots);
    }

    internal int? FindSlot(Guid? instance, string? name)
    {
        // IL-2 keeps the first three DirectInput instance groups and adds its own device suffix.
        if (instance is { } id)
        {
            string prefix = id.ToString("D")[..18];
            var byInstance = slots.Where(slot => slot.InstancePrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byInstance.Length == 1)
                return byInstance[0].Number;
            if (byInstance.Length > 1)
                return UniqueByName(byInstance, name);
        }
        return UniqueByName(slots, name);
    }

    private static int? UniqueByName(IEnumerable<Il2JoystickSlot> candidates, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var matching = candidates.Where(slot => slot.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matching.Length == 1 ? matching[0].Number : null;
    }

    internal static string? Encode(int slot, InputSignal signal)
    {
        if (signal.Kind == "button" && signal.Index is >= 1 and <= 128)
            return $"joy{slot}_b{signal.Index - 1}";
        if (signal.Kind == "axis" && signal.Index is >= 1 and <= 8)
            return $"joy{slot}_axis_{AxisLetters[signal.Index - 1]}";
        int direction = Array.IndexOf(Directions, signal.Direction);
        if (signal.Kind == "hat" && signal.Index is >= 1 and <= 4 && direction >= 0)
            return $"joy{slot}_pov{signal.Index - 1}_{direction * 90}";
        return null;
    }

    internal static (int Slot, InputSignal Signal)? Decode(string input)
    {
        var fields = input.Split('_');
        if (fields.Length is < 2 or > 3 || !fields[0].StartsWith("joy", StringComparison.Ordinal) ||
            !ReadNumber(fields[0].AsSpan(3), out int slot))
            return null;

        string control = fields[1];
        if (fields.Length == 2 && control.StartsWith('b') &&
            ReadNumber(control.AsSpan(1), out int button) && button < 128)
            return (slot, InputSignal.Button(button + 1));
        if (fields.Length == 3 && control == "axis" && fields[2].Length == 1 &&
            Array.IndexOf(AxisLetters, fields[2][0]) is int axis && axis >= 0)
            return (slot, InputSignal.Axis(axis + 1));
        if (fields.Length == 3 && control.StartsWith("pov", StringComparison.Ordinal) &&
            ReadNumber(control.AsSpan(3), out int hat) && hat < 4 &&
            ReadNumber(fields[2], out int angle) && angle < 360 && angle % 90 == 0)
            return (slot, InputSignal.Hat(hat + 1, Directions[angle / 90]));
        return null;
    }

    private static bool ReadNumber(ReadOnlySpan<char> text, out int number) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}
