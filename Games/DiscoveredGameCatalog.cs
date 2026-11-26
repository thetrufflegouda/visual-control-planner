using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace VisualControlsBinder.Games;

public static class DiscoveredGameCatalog
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxDepth = 64;
    private const int MaxActions = 20_000;
    private static readonly string[] ActionTables = ["actions", "inputActions", "controlActions"];
    private static readonly string[] ContextTables = ["actionMaps", "maps", "actionSets", "contexts", "categories"];
    private static readonly string[] Wrappers =
        ["input", "controls", "inputBindings", "keyBindings", "controlBindings", "bindingCatalog", "actionProfiles", "controlMappings"];
    private static readonly string[] ContextElements = ["actionMap", "map", "actionSet", "context", "category"];
    private static readonly string[] ActionElements = ["action", "inputAction", "controlAction"];
    private static readonly HashSet<string> AxisTypes = new(StringComparer.OrdinalIgnoreCase)
        { "axis", "analog", "analogAxis", "axis1D", "axis2D", "vector2", "vector3", "stick", "analogStick" };
    private static readonly Scope DefaultScope = new("Controls", "Controls", "");

    public static bool SupportsFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xml" or ".json" or ".inputactions";

    public static IReadOnlyList<GameAction> LoadActions(string path)
    {
        if (!SupportsFile(path))
            throw new InvalidDataException("Choose a readable XML, JSON, or Unity .inputactions file that explicitly lists game actions.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes)
            throw new InvalidDataException("This binding catalog is larger than 16 MB. Choose a smaller action list.");

        var actions = new ActionIndex();
        try
        {
            if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                ReadXml(stream, actions);
            else
            {
                Span<byte> prefix = stackalloc byte[3];
                int length = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
                if (length != prefix.Length || !prefix.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }))
                    stream.Position = 0;
                using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = MaxDepth });
                ReadJsonContainer(doc.RootElement, DefaultScope, actions);
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The selected JSON could not be read as a binding catalog. Choose another file.", error);
        }
        catch (XmlException error)
        {
            throw new InvalidDataException("The selected XML could not be read as a binding catalog. Choose another file.", error);
        }

        if (actions.Items.Count == 0)
            throw new InvalidDataException("No explicit game action list was found. Choose an XML action map or JSON file with an actions table.");
        return actions.Items;
    }

    private static void ReadXml(Stream stream, ActionIndex actions)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBytes
        };
        // The XML reader bounds document length, but has no maximum-depth option.
        using (var depthCheck = XmlReader.Create(stream, settings))
        {
            while (depthCheck.Read())
            {
                if (depthCheck.Depth > MaxDepth)
                    throw new InvalidDataException("This XML action list is too deeply nested.");
            }
        }
        stream.Position = 0;
        using var reader = XmlReader.Create(stream, settings);
        if (XDocument.Load(reader).Root is { } root)
            ReadXmlContainer(root, DefaultScope, actions);
    }

    private static void ReadXmlContainer(XElement element, Scope scope, ActionIndex actions)
    {
        string kind = element.Name.LocalName;
        bool context = OneOf(kind, ContextElements);
        bool actionTable = OneOf(kind, ActionTables);
        if (context)
            scope = Context(scope, XmlText(element, "id", "mapId"), XmlText(element, "name", "label"),
                XmlText(element, "category", "group"), XmlText(element, "section"));
        else if (actionTable)
            scope = scope with { Category = XmlText(element, "category", "group") ?? scope.Category };

        bool wrapper = OneOf(kind, Wrappers) || OneOf(kind, ContextTables);
        foreach (XElement child in element.Elements())
        {
            string name = child.Name.LocalName;
            if ((context || actionTable || wrapper) && OneOf(name, ActionElements))
            {
                string? id = XmlText(child, "id", "actionId", "commandId", "name");
                actions.Add(scope, id, XmlText(child, "displayName", "label", "uiLabel", "name"),
                    XmlText(child, "category", "group"), XmlText(child, "section"),
                    IsAxis(XmlText(child, "isAxis"), XmlText(child, "expectedControlType", "inputType", "type", "kind")));
            }
            else if (OneOf(name, ActionTables) || OneOf(name, ContextElements) || OneOf(name, ContextTables) || OneOf(name, Wrappers))
                ReadXmlContainer(child, scope, actions);
        }
    }

    private static void ReadJsonContainer(JsonElement element, Scope scope, ActionIndex actions)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (OneOf(property.Name, ActionTables))
                ReadJsonActions(property.Value, scope, actions);
            else if (OneOf(property.Name, ContextTables))
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in property.Value.EnumerateArray())
                        ReadJsonContext(item, null, scope, actions);
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty item in property.Value.EnumerateObject())
                        ReadJsonContext(item.Value, item.Name, scope, actions);
                }
            }
            else if (OneOf(property.Name, Wrappers))
                ReadJsonContainer(property.Value, scope, actions);
        }
    }

    private static void ReadJsonContext(JsonElement item, string? key, Scope scope, ActionIndex actions)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;
        Scope context = Context(scope, JsonText(item, "id", "mapId") ?? key,
            JsonText(item, "name", "label") ?? key, JsonText(item, "category", "group"), JsonText(item, "section"));
        ReadJsonContainer(item, context, actions);
    }

    private static void ReadJsonActions(JsonElement table, Scope scope, ActionIndex actions)
    {
        if (table.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in table.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    ReadJsonAction(item, null, scope, actions);
            }
        }
        else if (table.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty row in table.EnumerateObject())
            {
                if (row.Value.ValueKind == JsonValueKind.Object)
                    ReadJsonAction(row.Value, row.Name, scope, actions);
                else if (row.Value.ValueKind == JsonValueKind.String)
                    actions.Add(scope, row.Name, row.Value.GetString(), null, null, false);
            }
        }
    }

    private static void ReadJsonAction(JsonElement item, string? key, Scope scope, ActionIndex actions) =>
        actions.Add(scope, JsonText(item, "id", "actionId", "commandId") ?? key ?? JsonText(item, "name"),
            JsonText(item, "displayName", "label", "uiLabel", "name"),
            JsonText(item, "category", "group"), JsonText(item, "section"),
            IsAxis(JsonText(item, "isAxis"), JsonText(item, "expectedControlType", "inputType", "type", "kind")));

    private static Scope Context(Scope parent, string? id, string? name, string? category, string? section) =>
        new(id ?? name ?? parent.Map, category ?? name ?? parent.Category, section ?? parent.Section);

    private static string? XmlText(XElement element, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = element.Attributes().FirstOrDefault(attribute => !attribute.IsNamespaceDeclaration
                && attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
            value ??= element.Elements().FirstOrDefault(child => !child.HasElements
                && child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
            if (Text(value) is { } text)
                return text;
        }
        return null;
    }

    private static string? JsonText(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                string? value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True when name.Equals("isAxis", StringComparison.OrdinalIgnoreCase) => "true",
                    JsonValueKind.False when name.Equals("isAxis", StringComparison.OrdinalIgnoreCase) => "false",
                    JsonValueKind.Number when property.Value.TryGetInt64(out _) => property.Value.GetRawText(),
                    _ => null
                };
                if (Text(value) is { } text)
                    return text;
            }
        }
        return null;
    }

    private static string? Text(string? value)
    {
        string? text = value?.Trim();
        return text is { Length: > 0 and <= 512 } && !text.Any(char.IsControl) ? text : null;
    }

    private static bool IsAxis(string? flag, string? type) => flag switch
    {
        "1" => true,
        "0" => false,
        _ when bool.TryParse(flag, out bool explicitAxis) => explicitAxis,
        _ => type is not null && AxisTypes.Contains(type)
    };

    private static bool OneOf(string value, string[] names) => names.Contains(value, StringComparer.OrdinalIgnoreCase);

    private sealed record Scope(string Map, string Category, string Section);

    private sealed class ActionIndex
    {
        private readonly HashSet<(string Map, string Action)> _seen = [];
        public List<GameAction> Items { get; } = [];

        public void Add(Scope scope, string? id, string? name, string? category, string? section, bool axis)
        {
            id = Text(id);
            if (id is null || !_seen.Add((scope.Map, id)))
                return;
            if (Items.Count == MaxActions)
                throw new InvalidDataException("The file lists more than 20,000 game actions. Choose a smaller catalog.");
            Items.Add(new GameAction(scope.Map, id, category ?? scope.Category, section ?? scope.Section, Text(name) ?? id, axis));
        }
    }
}
