using System.IO;
using System.Windows;
using Microsoft.Win32;
using VisualControlsBinder.Games;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private async void NewProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game)
            return;
        try
        {
            var path = OutputFor(game) == OutputMode.JoystickGremlin ? CreateGremlinProfile(game) : CreateNativeProfile(game);
            if (path is null)
                return;
            await LoadGame();
            Notify($"Created {Path.GetFileName(path)}.");
        }
        catch (Exception error) { Notify(error.Message); }
    }

    private string? CreateGremlinProfile(GameTarget game)
    {
        var destination = PickNewProfilePath("Save a blank Joystick Gremlin profile", "Gremlin profile (*.xml)|*.xml",
            $"{ScopeOf(game).Replace('/', '-')}-vcp.xml", GremlinProfileDirectory(game));
        if (destination is null)
            return null;

        RefreshConnectedDevices();
        var devices = PlannedLayouts().Select(ConnectedDevice).OfType<Inputs.JoystickDevice>()
            .Where(device => !device.Name.StartsWith("vJoy", StringComparison.OrdinalIgnoreCase))
            .Select(device => (device.Id, device.Name)).DistinctBy(device => device.Id).ToArray();
        GremlinProfile.CreateBlank(destination, devices);
        SelectNewGremlinProfile(game, destination);
        return destination;
    }

    private string? CreateNativeProfile(GameTarget game)
    {
        if (game.Kind == GameKind.Dcs)
            return CreateDcsProfile();
        if (game.IsSwg)
            return CopySwgProfile(game);
        if (Il2NativeRestriction(game) is { } restriction)
            throw new InvalidOperationException(restriction);

        string filter = game.IsStarCitizen ? "Star Citizen profile (*.xml)|*.xml" : FlightProfileFilter(game.Kind);
        var source = CurrentProfilePath(game);
        if (!File.Exists(source))
        {
            var template = new OpenFileDialog
            {
                Title = $"Choose a {game.Name} profile template", Filter = filter, InitialDirectory = ProfileDialogDirectory(game)
            };
            if (template.ShowDialog(this) != true)
                return null;
            source = template.FileName;
        }
        var writer = VerifiedProfileTemplate(game, source);
        string name = game.Kind switch
        {
            GameKind.StarCitizen => "layout_VCP_exported.xml", GameKind.Il2 => "VCP.actions",
            GameKind.WarThunder => "VCP.blk", GameKind.Elite => "VCP.4.2.binds",
            GameKind.CliffsOfDover => "VCP-controls.ini",
            _ => throw new InvalidOperationException($"Native profiles are unavailable for {game.Name}.")
        };

        var destination = PickNewProfilePath($"Save a new {game.Name} profile", filter, name,
            NativeProfileDirectory(game, game.Kind == GameKind.WarThunder ? source : null));
        if (destination is null)
            return null;
        if (game.Kind == GameKind.Il2)
            RequireIl2GlobalDestination(GameFolder(game), destination);
        writer(destination);
        _plan.SourceProfiles[ScopeOf(game)] = destination;
        return destination;
    }

    private Action<string> VerifiedProfileTemplate(GameTarget game, string source) => game.Kind switch
    {
        GameKind.StarCitizen => StarCitizenGame.LoadProfile(source).SaveAs,
        GameKind.WarThunder => WarThunderProfile.Load(source).SaveAs,
        GameKind.Elite => EliteDangerousGame.LoadProfile(source).SaveAs,
        GameKind.Il2 => Il2Profile.Load(source).SaveAs,
        GameKind.CliffsOfDover => destination =>
            NativeBindingWrites.Commit([CliffsBindingProfile.Load(source).ToWrite(destination)]),
        _ => throw new InvalidOperationException($"Native profiles are unavailable for {game.Name}.")
    };

    private string? CopySwgProfile(GameTarget game)
    {
        var catalog = _profiles.SwgCatalog ?? throw new InvalidOperationException("Choose an SWG character keymap to use as a template.");
        var template = SwgNativeProfile.Load(catalog.SourceFiles);
        var picker = new OpenFolderDialog { Title = "Choose a folder for the new SWG keymap files", InitialDirectory = SwgProfileDirectory(game) };
        if (picker.ShowDialog(this) != true)
            return null;
        template.SaveAs(picker.FolderName);
        var path = Path.Combine(picker.FolderName, Path.GetFileName(catalog.SourceFiles[0]));
        _plan.CharacterProfiles[game.Id] = path;
        return path;
    }

    private string? PickNewProfilePath(string title, string filter, string filename, string folder)
    {
        var picker = new SaveFileDialog
        {
            Title = title, Filter = filter, FileName = filename, DefaultExt = Path.GetExtension(filename),
            InitialDirectory = folder,
            OverwritePrompt = false
        };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }
}
