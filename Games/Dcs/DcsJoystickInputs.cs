using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Games;

internal static class DcsJoystickInputs
{
    public static string? Encode(InputSignal signal) => signal.Kind switch
    {
        "button" => $"JOY_BTN{signal.Index}",
        "axis" when signal.Index is 7 or 8 => $"JOY_SLIDER{signal.Index - 6}",
        "axis" => $"JOY_{signal.Token.ToUpperInvariant()}",
        "hat" when signal.Direction is { Length: > 0 } direction => $"JOY_BTN_POV{signal.Index}_{char.ToUpperInvariant(direction[0])}",
        _ => null
    };

    public static InputSignal? Parse(string key)
    {
        if (key.StartsWith("JOY_BTN", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(7), out int button) && button is >= 1 and <= 128)
            return InputSignal.Button(button);

        if (key.StartsWith("JOY_BTN_POV", StringComparison.Ordinal) && key.Length == 14 && key[12] == '_' &&
            key[11] is >= '1' and <= '4')
        {
            string? direction = key[13] switch
            {
                'U' => "up", 'R' => "right", 'D' => "down", 'L' => "left", _ => null
            };
            return direction is null ? null : InputSignal.Hat(key[11] - '0', direction);
        }

        return key switch
        {
            "JOY_X" => InputSignal.Axis(1),
            "JOY_Y" => InputSignal.Axis(2),
            "JOY_Z" => InputSignal.Axis(3),
            "JOY_RX" => InputSignal.Axis(4),
            "JOY_RY" => InputSignal.Axis(5),
            "JOY_RZ" => InputSignal.Axis(6),
            "JOY_SLIDER1" => InputSignal.Axis(7),
            "JOY_SLIDER2" => InputSignal.Axis(8),
            _ => null
        };
    }
}
