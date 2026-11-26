using System.IO;
using System.Xml;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder.Games;

internal sealed record GameProfiles
{
    public StarCitizenProfile? StarCitizen { get; init; }
    public SwgCatalog? SwgCatalog { get; init; }
    public SwgNativeProfile? SwgNative { get; init; }
    public EliteDangerousProfile? Elite { get; init; }
    public WarThunderProfile? WarThunder { get; init; }
    public (string Folder, string? Path)? WarThunderSource { get; init; }
    public SquadronsProfile? Squadrons { get; init; }
    public IReadOnlyList<SquadronsBinding> SquadronsBindings { get; init; } = [];
    public CliffsBindingProfile? Cliffs { get; init; }
    public MsfsBindingProfile[] Msfs { get; init; } = [];
    public IReadOnlyList<GameBinding> Il2Bindings { get; init; } = [];
    public Il2JoystickSlots? Il2Slots { get; init; }
    public GremlinProfile? Gremlin { get; init; }

    public static string? FindWarThunderProfile(string folder) =>
        WarThunderGame.FindActiveProfile() ?? WarThunderGame.FindProfile(folder);

    public static IReadOnlyList<GameAction> ReadActions(GameKind kind, string folder, string? source) => kind switch
    {
        GameKind.Squadrons => SquadronsGame.LoadActions(source),
        GameKind.Msfs2020 or GameKind.Msfs2024 => MsfsGame.LoadActions(folder, source, kind == GameKind.Msfs2024),
        GameKind.Discovered => DiscoveredGameCatalog.LoadActions(source ??
            throw new InvalidOperationException("Choose a binding file to load this game's actions.")),
        GameKind.CliffsOfDover => CliffsOfDoverGame.LoadActions(folder, source),
        GameKind.WarThunder => WarThunderGame.LoadActions(folder, source),
        GameKind.Elite => EliteDangerousGame.LoadActions(folder, source),
        _ => []
    };

    public GameProfiles ReadNative(GameKind kind, string folder, string? path,
        IReadOnlyList<GameAction> actions, bool globalIl2)
    {
        if (kind == GameKind.Swg && SwgCatalog is { } swg)
            return this with { SwgNative = SwgNativeProfile.Load(swg.SourceFiles) };
        if (kind is GameKind.Msfs2020 or GameKind.Msfs2024)
            return this with { Msfs = ReadMsfsProfiles(folder, kind == GameKind.Msfs2024, path) };
        if (path is null || !File.Exists(path))
            return this;

        switch (kind)
        {
            case GameKind.StarCitizen:
                return this with { StarCitizen = StarCitizenGame.LoadProfile(path) };
            case GameKind.Elite:
                return this with { Elite = EliteDangerousGame.LoadProfile(path) };
            case GameKind.WarThunder:
                return this with { WarThunder = WarThunderProfile.Load(path) };
            case GameKind.Squadrons:
                var squadrons = SquadronsProfile.Load(path);
                return this with { Squadrons = squadrons, SquadronsBindings = squadrons.Bindings(actions).ToArray() };
            case GameKind.CliffsOfDover:
                return this with { Cliffs = CliffsBindingProfile.Load(path) };
            case GameKind.Il2:
                return this with
                {
                    Il2Bindings = globalIl2 ? Il2Profile.Load(path).Bindings : Il2Game.LoadBindings(path),
                    Il2Slots = Il2JoystickSlots.Load(folder)
                };
            default:
                return this;
        }
    }

    private static MsfsBindingProfile[] ReadMsfsProfiles(string folder, bool is2024, string? selected)
    {
        var paths = new[] { selected }.OfType<string>().Concat(MsfsGame.FindProfiles(folder, is2024))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var profiles = new List<MsfsBindingProfile>();
        foreach (string path in paths)
        {
            try { profiles.Add(MsfsBindingProfile.Load(path)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
            {
                if (path == selected)
                    throw;
            }
        }
        return profiles.ToArray();
    }
}
