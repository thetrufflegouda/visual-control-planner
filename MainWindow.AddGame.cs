using System.IO;
using System.Windows;
using Microsoft.Win32;
using VisualControlsBinder.Games;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private IReadOnlyList<AddedGame> _addedGames = [];

    private AddedGame? FindAddedGame(GameTarget game) => _addedGames.FirstOrDefault(item => item.Id == game.Id);

    private void PopulateGamePicker(string? selected = null)
    {
        var games = GameTarget.All.Select(target => FindAddedGame(target) is { } added
                ? target with { Folder = added.InstallFolder } : target)
            .Concat(_addedGames.Where(game => game.BindingFile is not null)
                .Select(game => new GameTarget(game.Id, game.Name, game.InstallFolder, GameKind.Discovered)))
            .OrderBy(game => game.Kind == GameKind.CustomVJoy)
            .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        GamePicker.ItemsSource = games;
        GamePicker.SelectedItem = games.FirstOrDefault(game => game.Id == selected)
            ?? games.First(game => game.Id == GameTarget.All[0].Id);
    }

    private async Task ChangeDiscoveredFile(GameTarget game)
    {
        if (FindAddedGame(game) is not { } added)
            return;
        var dialog = new OpenFileDialog
        {
            Title = $"Choose binding data for {added.Name}",
            Filter = "Binding data (*.xml;*.json;*.inputactions)|*.xml;*.json;*.inputactions|All files|*.*",
            InitialDirectory = added.BindingFile is { } previous ? Path.GetDirectoryName(previous) : added.InstallFolder
        };
        if (dialog.ShowDialog(this) != true)
            return;
        var path = dialog.FileName;
        try
        {
            await Task.Run(() => DiscoveredGameCatalog.LoadActions(path));
            _addedGames = AddedGameStore.Save(added with { BindingFile = path });
            PopulateGamePicker(game.Id);
        }
        catch (Exception error) { Notify(error.Message); }
    }
}
