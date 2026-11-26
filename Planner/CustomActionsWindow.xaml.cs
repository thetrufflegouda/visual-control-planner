using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VisualControlsBinder.Games;

namespace VisualControlsBinder.Planner;

internal sealed partial class CustomActionsWindow : Window
{
    private readonly ObservableCollection<Draft> _rows;

    public CustomActionsWindow(IEnumerable<GameAction> actions)
    {
        _rows = new(actions.Select(action => Draft.FromAction(action)));
        InitializeComponent();
        ActionList.ItemsSource = _rows;
    }

    public IReadOnlyList<GameAction> Result { get; private set; } = [];

    private void AddAction(object sender, RoutedEventArgs e)
    {
        var row = new Draft("Controls", "", "Button");
        _rows.Add(row);
        ActionList.ScrollIntoView(row);
        EditorStatus.Text = "";
    }

    private void RemoveAction(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Draft row })
            _rows.Remove(row);
    }

    private async void ImportActions(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open an action list",
            Filter = "Action lists (*.txt;*.xml;*.json;*.inputactions)|*.txt;*.xml;*.json;*.inputactions"
        };
        if (dialog.ShowDialog(this) != true || sender is not Button import)
            return;
        var path = dialog.FileName;
        try
        {
            import.IsEnabled = false;
            var actions = await Task.Run(() => ReadActions(path));
            var known = _rows.Select(row => row.Action()).ToHashSet(GameActionLabelComparer.Instance);
            foreach (var action in actions.Where(known.Add))
                _rows.Add(Draft.FromAction(action));
            EditorStatus.Text = "";
        }
        catch (Exception error) { EditorStatus.Text = error.Message; }
        finally { import.IsEnabled = true; }
    }

    private void SaveActions(object sender, RoutedEventArgs e)
    {
        var actions = _rows.Select(row => row.Action()).ToArray();
        if (actions.Any(action => action.Name.Length == 0))
        {
            EditorStatus.Text = "Enter a name for each action.";
            return;
        }
        if (actions.Distinct(GameActionLabelComparer.Instance).Count() != actions.Length)
        {
            EditorStatus.Text = "Give duplicate actions a different name or category.";
            return;
        }
        Result = actions;
        DialogResult = true;
    }

    private static IReadOnlyList<GameAction> ReadActions(string path)
    {
        if (DiscoveredGameCatalog.SupportsFile(path))
            return DiscoveredGameCatalog.LoadActions(path).Select(action => Draft.FromAction(action, newIdentity: true).Action()).ToArray();
        if (!Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Choose a text, XML, JSON, or Unity Input System action list under 16 MB.");
        return File.ReadLines(path).Select(line => line.Trim()).Where(line => line.Length > 0)
            .Select(line => new Draft("Controls", line, "Button").Action()).ToArray();
    }

    private sealed class Draft(string category, string name, string type, string? id = null)
    {
        private readonly string _id = id ?? Guid.NewGuid().ToString("N");
        public string Category { get; set; } = category;
        public string Name { get; set; } = name;
        public string InputType { get; set; } = type;
        public string[] InputTypes { get; } = ["Button", "Axis"];

        public GameAction Action() => new("Custom", _id, string.IsNullOrWhiteSpace(Category) ? "Controls" : Category.Trim(),
            "", Name.Trim(), InputType == "Axis");

        public static Draft FromAction(GameAction action, bool newIdentity = false) => new(
            string.Join(" / ", new[] { action.Category, action.Section }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct()),
            action.Name, action.IsAxis ? "Axis" : "Button", newIdentity ? null : action.Id);
    }

    private sealed class GameActionLabelComparer : IEqualityComparer<GameAction>
    {
        public static readonly GameActionLabelComparer Instance = new();
        private static readonly StringComparer Labels = StringComparer.OrdinalIgnoreCase;
        public bool Equals(GameAction? x, GameAction? y) => x is not null && y is not null &&
            x.IsAxis == y.IsAxis && Labels.Equals(x.Category, y.Category) && Labels.Equals(x.Name, y.Name);
        public int GetHashCode(GameAction action) => HashCode.Combine(Labels.GetHashCode(action.Category), Labels.GetHashCode(action.Name), action.IsAxis);
    }
}
