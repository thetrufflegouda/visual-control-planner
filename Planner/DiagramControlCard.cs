using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using VisualControlsBinder.Hardware;

namespace VisualControlsBinder.Planner;

internal static class DiagramControlCard
{
    private static readonly (string Direction, int Row, int Column, string Glyph)[] HatCells =
    [
        ("up", 0, 1, "▲"),
        ("left", 1, 0, "◀"),
        ("press", 1, 1, "●"),
        ("right", 1, 2, "▶"),
        ("down", 2, 1, "▼")
    ];

    private static readonly Brush Mode2 = ColorBrush(183, 211, 218);
    private static readonly Brush Mode3 = ColorBrush(232, 196, 127);
    private static readonly Brush Mode4 = ColorBrush(179, 206, 162);
    private static readonly Brush Mode5 = ColorBrush(211, 190, 217);

    public static bool IsDirectionalHat(IReadOnlyList<HardwareControl> gestures)
    {
        if (gestures.Count is < 4 or > 5)
            return false;
        var directions = gestures.Select(gesture => Suffix(gesture.Id)).ToHashSet(StringComparer.Ordinal);
        return directions.Count == gestures.Count && directions.IsSupersetOf(["up", "right", "down", "left"]) &&
            directions.All(direction => HatCells.Any(cell => cell.Direction == direction));
    }

    public static FrameworkElement Create(IReadOnlyList<HardwareControl> gestures, IReadOnlyList<DiagramBinding> rows,
        string? heading = null, int? marker = null)
    {
        bool hat = IsDirectionalHat(gestures);
        var content = new StackPanel();
        heading ??= DefaultHeading(gestures, hat);
        if (marker is { } number)
            heading = $"{number} · {heading}";
        if (!string.IsNullOrWhiteSpace(heading))
        {
            var title = Text(heading.ToUpperInvariant(), Resource("TextSecondary"), 11);
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(2, 0, 2, 3);
            content.Children.Add(title);
        }
        var byGesture = rows.Where(row => row.Address is not null)
            .ToLookup(row => row.Address!.Control, StringComparer.Ordinal);
        if (hat)
            content.Children.Add(CreateHat(gestures, byGesture));
        else
            AddGeneralLabels(content, gestures, byGesture);

        bool modifier = rows.Any(row => row.ShiftModifier is not null);
        return new Border
        {
            Child = content, Background = Resource("PanelBackground"),
            BorderBrush = Resource(modifier ? "GremlinShiftAccent" : "PanelBorder"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(2, 3, 2, 3)
        };
    }

    private static Grid CreateHat(IEnumerable<HardwareControl> gestures, ILookup<string, DiagramBinding> byGesture)
    {
        var cross = new Grid();
        for (int i = 0; i < 3; i++)
        {
            cross.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cross.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var directions = gestures.ToDictionary(gesture => Suffix(gesture.Id), StringComparer.Ordinal);
        foreach (var (direction, row, column, glyph) in HatCells)
        {
            if (!directions.TryGetValue(direction, out var control))
                continue;
            var arm = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var indicator = Text(glyph, Resource("TextSecondary"), 10);
            indicator.FontFamily = new FontFamily("Segoe UI Symbol");
            indicator.TextAlignment = TextAlignment.Center;
            arm.Children.Add(indicator);
            AddActionLabels(arm, byGesture[control.Id], center: true);
            var cell = new Border
            {
                Child = arm, Margin = new Thickness(1), Padding = new Thickness(3, 2, 3, 3),
                Background = Resource("ControlBackground"), CornerRadius = new CornerRadius(3), MinHeight = 19
            };
            AutomationProperties.SetName(cell, control.Name);
            if (!byGesture[control.Id].Any())
                GiveDetails(cell, control.Name);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            cross.Children.Add(cell);
        }
        return cross;
    }

    private static void AddGeneralLabels(StackPanel panel, IReadOnlyList<HardwareControl> gestures,
        ILookup<string, DiagramBinding> byGesture)
    {
        foreach (var gesture in gestures)
        {
            var bindings = byGesture[gesture.Id];
            if (!bindings.Any())
                continue;
            if (gestures.Count > 1)
            {
                var label = Text(ShortGesture(gesture), Resource("TextSecondary"), 10);
                label.Margin = new Thickness(2, 2, 2, 0);
                panel.Children.Add(label);
            }
            AddActionLabels(panel, bindings, center: false);
        }
    }

    private static void AddActionLabels(StackPanel panel, IEnumerable<DiagramBinding> rows, bool center)
    {
        var groups = rows.SelectMany(row => row.Address!.Layer.Split(" / ",
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(layer => (Row: row, Layer: layer)))
            .GroupBy(item => (item.Layer, item.Row.Action?.Map, item.Row.Action?.Id, item.Row.Activation,
                item.Row.Inherited, Output: item.Row.Action is null ? item.Row.Label : null))
            .OrderBy(group => LayerOrder(group.Key.Layer)).ToArray();
        var names = groups.ToLookup(group => (group.Key.Layer, group.First().Row.Label));
        foreach (var group in groups)
        {
            var layer = group.Key.Layer;
            var row = group.First().Row;
            var caption = row.Action is { } action && names[(layer, row.Label)].Count() > 1
                ? $"{row.Label} · {SectionOf(action)}" : row.Label;
            if (row.Inherited)
                caption += " · inherited";
            var label = Text(caption, BindingBrush(row, layer), center ? 11 : 12);
            label.TextAlignment = center ? TextAlignment.Center : TextAlignment.Left;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            label.LineHeight = center ? 13 : 15;
            label.MaxHeight = label.LineHeight * 2;
            label.Margin = center ? new Thickness(0, 1, 0, 0) : new Thickness(2, 1, 2, 1);
            if (row.ShiftModifier is not null)
                label.FontWeight = FontWeights.SemiBold;
            AutomationProperties.SetName(label, $"{DisplayLayer(layer)} · {caption}");
            GiveDetails(label, string.Join(Environment.NewLine + Environment.NewLine,
                group.Select(item => AssignmentDetails(item.Row)).Distinct(StringComparer.Ordinal)));
            panel.Children.Add(label);
        }
    }

    private static string SectionOf(Games.GameAction action) => !string.IsNullOrWhiteSpace(action.Section) ? action.Section
        : !string.IsNullOrWhiteSpace(action.Category) ? action.Category : action.Map;

    private static string AssignmentDetails(DiagramBinding row)
    {
        var sources = new[] { row.Action?.Category, row.Action?.Section }
            .Where(segment => !string.IsNullOrWhiteSpace(segment)).Distinct();
        string context = string.Join(" / ", sources);
        string? placement = row.Reference ? "Reference placement" : null;
        string? layer = row.Address is { } address ? DisplayLayer(address.Layer) : null;
        return string.Join(Environment.NewLine, new[] { row.Label, layer, context, row.InputDetail, placement, row.InheritanceDetail }
            .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static void GiveDetails(FrameworkElement element, string text)
    {
        var body = Text(text, Resource("TextPrimary"), 12);
        body.MaxWidth = 340;
        element.ToolTip = new ToolTip
        {
            Content = body, Padding = new Thickness(7), Background = Resource("PanelBackground"),
            BorderBrush = Resource("PanelBorder")
        };
        ToolTipService.SetShowDuration(element, 15000);
        AutomationProperties.SetHelpText(element, text);
    }

    private static TextBlock Text(string label, Brush foreground, double fontSize) =>
        new() { Text = label, Foreground = foreground, FontSize = fontSize, TextWrapping = TextWrapping.Wrap };

    private static int LayerOrder(string layer) => layer switch
    {
        "Normal" or "Mode 1" => 0,
        "Shift" or "Mode 2" => 1,
        "Mode 3" => 2,
        "Mode 4" => 3,
        "Mode 5" => 4,
        _ => 5
    };

    public static Brush LayerBrush(string layer) => layer switch
    {
        "Shift" => Resource("ShiftAccent"),
        GremlinShiftSetting.Layer => Resource("GremlinShiftAccent"),
        "Mode 2" => Mode2,
        "Mode 3" => Mode3,
        "Mode 4" => Mode4,
        "Mode 5" => Mode5,
        _ => Resource("TextPrimary")
    };

    public static string DisplayLayer(string layer) => layer == "Shift" ? "Hardware Shift" : layer;

    public static Brush BindingBrush(DiagramBinding row, string layer) => row.ShiftModifier is not null
        ? Resource("GremlinShiftAccent") : row.Inherited ? Resource("TextSecondary") : LayerBrush(layer);

    private static string DefaultHeading(IReadOnlyList<HardwareControl> gestures, bool hat)
    {
        if (gestures.Count == 0)
            return "";
        var gesture = gestures[0];
        if (hat && gesture.Name.EndsWith($" {Suffix(gesture.Id)}", StringComparison.OrdinalIgnoreCase))
            return gesture.Name[..^(Suffix(gesture.Id).Length + 1)];
        if (gestures.Count > 1)
        {
            for (int end = gesture.Name.LastIndexOf(' '); end > 0; end = gesture.Name.LastIndexOf(' ', end - 1))
            {
                var prefix = gesture.Name[..end];
                if (gestures.Skip(1).All(item => item.Name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)))
                    return prefix;
            }
        }
        return gesture.Name;
    }

    private static string ShortGesture(HardwareControl gesture) => Suffix(gesture.Id) switch
    {
        "first" => "First detent",
        "second" => "Second detent",
        "up" => "Up",
        "down" => "Down",
        "left" => "Left",
        "right" => "Right",
        "press" => "Press",
        "center" => "Center",
        "cw" => "Clockwise",
        "ccw" => "Counterclockwise",
        "x" or "horizontal" => "X axis",
        "y" or "vertical" => "Y axis",
        _ => gesture.Name
    };

    private static string Suffix(string id) => id[(id.LastIndexOf('-') + 1)..];

    private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);

    private static Brush ColorBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
