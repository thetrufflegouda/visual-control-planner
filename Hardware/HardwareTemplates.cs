using System.IO;
using System.Text.Json;

namespace VisualControlsBinder.Hardware;

public sealed record HardwareDefinition(
    string Id, string Name, int VendorId, int ProductId,
    IReadOnlyList<HardwareView> Views, IReadOnlyList<HardwareControl> Controls)
{
    public string? TemplateId { get; init; }
    public string? Manufacturer { get; init; }
    public string ModelId => TemplateId ?? Id;
    public bool IsBase { get; init; }
    public bool IsPanel { get; init; }
    public bool IsAssemblyPart => IsBase || IsPanel;
    public bool MirrorGrip { get; init; }
    public IReadOnlyList<int> AlternateProductIds { get; init; } = [];
    public IReadOnlyList<string> CompatibleBaseIds { get; init; } = [];
    public IReadOnlyList<string> CompatiblePanelIds { get; init; } = [];
    public string? BaseLayoutId { get; init; }
    public bool GripOnly => BaseLayoutId == "";
    internal bool MatchesReferenceProduct((int Vendor, int Product) ids) =>
        ids.Vendor == VendorId && (ids.Product == ProductId || AlternateProductIds.Contains(ids.Product));
    public bool IsViewMirrored(HardwareView view) =>
        MirrorGrip && !view.Id.StartsWith("base-", StringComparison.Ordinal);
}

public sealed record HardwareView(
    string Id, string Name, string Image, IReadOnlyList<HardwareHotspot> Hotspots);

public sealed record HardwareHotspot(string ControlId, double X, double Y, double Width, double Height);

public sealed record HardwareControl(
    string Id, string Name, string Kind, string? SuggestedInput, string? Location);

public static class HardwareTemplates
{
    private static readonly string AssetRoot = FindAssetRoot();

    internal static string AssetPath(string relativePath) => Path.Combine(AssetRoot, relativePath);

    private static string FindAssetRoot()
    {
        var support = Path.Combine(AppContext.BaseDirectory, "Support");
        return File.Exists(Path.Combine(support, "Hardware", "layouts.json"))
            ? support : AppContext.BaseDirectory;
    }

    public static HardwareDefinition Assemble(HardwareDefinition grip, HardwareDefinition? flightstickBase) =>
        Assemble(grip, flightstickBase, null, null);

    public static HardwareDefinition Assemble(HardwareDefinition grip, HardwareDefinition? flightstickBase,
        HardwareDefinition? leftPanel, HardwareDefinition? rightPanel)
    {
        if (flightstickBase is not null && (!flightstickBase.IsBase ||
            !grip.CompatibleBaseIds.Contains(flightstickBase.Id, StringComparer.Ordinal)))
            throw new ArgumentException($"{flightstickBase.Name} is incompatible with {grip.Name}.", nameof(flightstickBase));

        var baseControls = flightstickBase?.Controls ?? [];
        var transferred = baseControls.Select(control => control.Id).ToHashSet(StringComparer.Ordinal);
        if (flightstickBase is null)
            transferred.UnionWith(["stick-x", "stick-y"]);
        var gripViews = grip.Views.Select(view => view with
        {
            Hotspots = view.Hotspots.Where(spot => !transferred.Contains(spot.ControlId)).ToArray()
        });
        var baseViews = (flightstickBase?.Views ?? []).Select(view => view with { Id = $"base-{view.Id}" });
        var left = MountPanel(flightstickBase, leftPanel, "Left");
        var right = MountPanel(flightstickBase, rightPanel, "Right");
        return grip with
        {
            BaseLayoutId = flightstickBase?.Id ?? "",
            Name = flightstickBase is null ? $"{grip.Name} — GRIP ONLY" : $"{grip.Name} — {flightstickBase.Name}",
            Views = [.. gripViews, .. baseViews, .. left.Views, .. right.Views],
            Controls = [.. grip.Controls.Where(control => !transferred.Contains(control.Id)),
                .. baseControls, .. left.Controls, .. right.Controls]
        };
    }

    private static (IReadOnlyList<HardwareView> Views, IReadOnlyList<HardwareControl> Controls) MountPanel(
        HardwareDefinition? flightstickBase, HardwareDefinition? panel, string side)
    {
        if (panel is null)
            return ([], []);
        if (!panel.IsPanel || flightstickBase?.CompatiblePanelIds.Contains(panel.Id, StringComparer.Ordinal) != true)
            throw new ArgumentException($"{panel.Name} cannot be attached to this base.", nameof(panel));

        string prefix = $"{side.ToLowerInvariant()}-panel-{panel.Id}-";
        var views = panel.Views.Select(view => view with
        {
            Id = $"base-{prefix}{view.Id}",
            Name = $"{side} · {view.Name}",
            Hotspots = view.Hotspots.Select(spot => spot with { ControlId = prefix + spot.ControlId }).ToArray()
        }).ToArray();
        var controls = panel.Controls.Select(control => control with
        {
            Id = prefix + control.Id,
            Name = $"{side} · {control.Name}",
            Location = control.Location is null ? null : prefix + control.Location
        }).ToArray();
        return (views, controls);
    }

    public static IReadOnlyList<HardwareDefinition> Load()
    {
        var directory = AssetPath("Hardware");
        var paths = new List<string> { Path.Combine(directory, "layouts.json") };
        var library = Path.Combine(directory, "Library");
        if (Directory.Exists(library))
            paths.AddRange(Directory.EnumerateFiles(library, "*.json").Order(StringComparer.Ordinal));
        var catalogs = paths.Select(ReadCatalog).ToArray();
        var layouts = catalogs.SelectMany(catalog => catalog.Layouts).ToDictionary(layout => layout.Id, StringComparer.Ordinal);
        foreach (var layout in layouts.Values)
            Validate(layout);

        var devices = catalogs.SelectMany(catalog => catalog.Devices.Select(device =>
        {
            if (!layouts.TryGetValue(device.Layout, out var layout))
                throw new InvalidDataException($"Missing layout '{device.Layout}' for hardware '{device.Id}'.");

            return new HardwareDefinition(device.Id, device.Name, device.VendorId, device.ProductId,
                layout.Views, layout.Controls)
            {
                IsBase = device.IsBase,
                IsPanel = device.IsPanel,
                MirrorGrip = device.MirrorGrip,
                Manufacturer = device.Manufacturer ?? catalog.Manufacturer ?? "Other manufacturers",
                AlternateProductIds = device.AlternateProductIds ?? [],
                CompatibleBaseIds = device.CompatibleBaseIds ?? [],
                CompatiblePanelIds = device.CompatiblePanelIds ?? []
            };
        })).ToDictionary(device => device.Id, StringComparer.Ordinal);
        foreach (var grip in devices.Values)
        {
            foreach (var id in grip.CompatibleBaseIds)
                if (!devices.TryGetValue(id, out var flightstickBase) || !flightstickBase.IsBase)
                    throw new InvalidDataException($"Missing compatible flightstick base '{id}' for '{grip.Id}'.");
            foreach (var id in grip.CompatiblePanelIds)
                if (!grip.IsBase || !devices.TryGetValue(id, out var panel) || !panel.IsPanel)
                    throw new InvalidDataException($"Missing compatible panel '{id}' for '{grip.Id}'.");
        }
        return devices.Values.ToArray();
    }

    private static Catalog ReadCatalog(string path)
    {
        using var file = File.OpenRead(path);
        return JsonSerializer.Deserialize<Catalog>(file, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"The hardware layout catalog is empty: {Path.GetFileName(path)}.");
    }

    private static void Validate(Layout layout)
    {
        var controls = layout.Controls.ToDictionary(control => control.Id, StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in layout.Views)
        {
            if (!File.Exists(AssetPath(view.Image)))
                throw new InvalidDataException($"Missing hardware photo '{view.Image}'.");

            foreach (var spot in view.Hotspots)
            {
                if (!controls.TryGetValue(spot.ControlId, out var control))
                    throw new InvalidDataException($"Unknown control '{spot.ControlId}' in view '{view.Id}'.");
                if (spot.Width <= 0 || spot.Height <= 0 || spot.X - spot.Width / 2 < 0 ||
                    spot.X + spot.Width / 2 > 1 || spot.Y - spot.Height / 2 < 0 || spot.Y + spot.Height / 2 > 1)
                    throw new InvalidDataException($"Invalid photo region for '{spot.ControlId}' in view '{view.Id}'.");

                reachable.Add(control.Location ?? control.Id);
            }
        }

        if (layout.Controls.Any(control => !reachable.Contains(control.Location ?? control.Id)))
            throw new InvalidDataException($"Hardware layout '{layout.Id}' contains controls that do not appear in any view.");
    }

    private sealed record Catalog(IReadOnlyList<Device> Devices, IReadOnlyList<Layout> Layouts, string? Manufacturer = null);
    private sealed record Device(string Id, string Name, int VendorId, int ProductId, string Layout,
        bool IsBase = false, IReadOnlyList<string>? CompatibleBaseIds = null, bool MirrorGrip = false,
        string? Manufacturer = null, IReadOnlyList<int>? AlternateProductIds = null, bool IsPanel = false,
        IReadOnlyList<string>? CompatiblePanelIds = null);
    private sealed record Layout(string Id, IReadOnlyList<HardwareView> Views, IReadOnlyList<HardwareControl> Controls);
}
