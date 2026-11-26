using System.Text.RegularExpressions;

namespace VisualControlsBinder.Games;

internal static partial class FlightActionNames
{
    public static string FromId(string id)
    {
        var label = NameBoundary().Replace(id.Replace('_', ' '), " ").Trim();
        return label.Length == 0 ? id : char.ToUpperInvariant(label[0]) + label[1..];
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex NameBoundary();
}
