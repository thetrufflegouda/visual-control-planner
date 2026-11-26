using System.IO;
using VisualControlsBinder.Games;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private string ProfileDialogDirectory(GameTarget game)
    {
        if (OutputFor(game) == OutputMode.JoystickGremlin)
            return GremlinProfileDirectory(game);
        if (game.IsMsfs)
            return NativeProfileDirectory(game, CurrentProfilePath(game));
        return game.IsSwg ? SwgProfileDirectory(game)
            : NativeProfileDirectory(game, _plan.SourceProfiles.GetValueOrDefault(ScopeOf(game)));
    }

    private string GremlinProfileDirectory(GameTarget game) =>
        ExistingProfileDirectory([Path.GetDirectoryName(GremlinProfilePath(game))]);

    private string NativeProfileDirectory(GameTarget game, string? preferredProfile = null)
    {
        if (Path.GetDirectoryName(preferredProfile) is { } preferred && Directory.Exists(preferred))
            return preferred;
        string install = GameFolder(game);
        string?[] defaults = game.Kind switch
        {
            GameKind.StarCitizen => [StarCitizenGame.MappingsDirectory(install)],
            GameKind.Swg => [.. SwgCatalog.ProfileFolders(install)],
            GameKind.Il2 => [Il2Game.BindingsDirectory(install)],
            GameKind.WarThunder => [Path.GetDirectoryName(WarThunderGame.FindActiveProfile()), WarThunderGame.SavesDirectory],
            GameKind.Elite => [EliteDangerousGame.BindingsDirectory, EliteDangerousGame.SchemeDirectory(install)],
            GameKind.CliffsOfDover => [CliffsOfDoverGame.BindingsDirectory],
            GameKind.Squadrons => [SquadronsGame.DefaultSettingsFolder],
            GameKind.Msfs2020 or GameKind.Msfs2024 => [MsfsGame.FindProfileFolder(install, game.Kind == GameKind.Msfs2024)],
            _ => []
        };
        return ExistingProfileDirectory([.. defaults, install]);
    }

    private string SwgProfileDirectory(GameTarget game) => NativeProfileDirectory(game,
        _profiles.SwgCatalog?.SourceFiles.FirstOrDefault() ?? _plan.CharacterProfiles.GetValueOrDefault(game.Id));

    private static string ExistingProfileDirectory(string?[] folders) =>
        folders.OfType<string>().FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}
