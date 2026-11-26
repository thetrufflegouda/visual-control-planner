using System.IO;
using Microsoft.Win32;
using VisualControlsBinder.Games;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private async Task EditCustomGameActions(GameTarget game)
    {
        var editor = new CustomActionsWindow(_plan.CustomActions) { Owner = this };
        if (editor.ShowDialog() != true)
            return;
        var updated = editor.Result.ToDictionary(action => action.Id, StringComparer.Ordinal);
        var affected = _plan.Edits.Where(edit => edit.Game == game.Id &&
            (!updated.TryGetValue(edit.Action.Id, out var replacement) || replacement.IsAxis != edit.Action.IsAxis)).ToHashSet();
        if (affected.Count > 0 && !ReviewWindow.Confirm(this, "Save custom actions",
            $"This change affects {affected.Count} assignments in the plan. Removed actions and actions with a new input type will be unassigned.", "Save actions"))
            return;
        _plan.CustomActions = editor.Result.ToList();
        _plan.Edits.RemoveAll(affected.Contains);
        for (int index = 0; index < _plan.Edits.Count; index++)
        {
            var edit = _plan.Edits[index];
            if (edit.Game == game.Id && updated.TryGetValue(edit.Action.Id, out var updatedAction))
                _plan.Edits[index] = edit with { Action = updatedAction };
        }
        await LoadGame();
    }

    private string? SquadronsProfile(GameTarget game) => _plan.SourceProfiles.GetValueOrDefault(game.Id) ?? SquadronsGame.FindProfile();

    private async Task ChooseSquadronsProfile(GameTarget game)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the Squadrons settings file", Filter = "Squadrons settings|ProfileOptions_profile_synced|All files|*.*",
            InitialDirectory = NativeProfileDirectory(game, SquadronsProfile(game))
        };
        if (dialog.ShowDialog(this) != true)
            return;
        string source = dialog.FileName;
        var plan = _plan;
        int request = _catalogRequest;
        try
        {
            await Task.Run(() => SquadronsGame.LoadActions(source));
        }
        catch (Exception error)
        {
            if (IsCurrentGameRequest(plan, game, request))
                Notify(error.Message);
            return;
        }
        if (!IsCurrentGameRequest(plan, game, request))
            return;
        plan.SourceProfiles[game.Id] = source;
        await LoadGame();
    }

    private async Task ChooseMsfsSource(GameTarget game)
    {
        const string profile = "Control profile or ActionDB";
        const string folder = "Settings folder";
        var choice = ReviewWindow.Pick(this, "Microsoft Flight Simulator control files", new[] { profile, folder });
        if (choice == profile)
        {
            var picker = new OpenFileDialog
            {
                Title = "Choose a flight simulator control profile",
                Filter = "Named control files|*.xml;*.actiondb;*.inputprofile;inputprofile_*|All files|*.*",
                FilterIndex = 2, // Store profiles have no extension.
                InitialDirectory = NativeProfileDirectory(game, _plan.SourceProfiles.GetValueOrDefault(game.Id))
            };
            if (picker.ShowDialog(this) != true)
                return;
            string path = picker.FileName;
            string gameFolder = GameFolder(game);
            var plan = _plan;
            int request = _catalogRequest;
            try { await Task.Run(() => MsfsGame.LoadActions(gameFolder, path, game.Kind == GameKind.Msfs2024)); }
            catch (Exception error)
            {
                if (IsCurrentGameRequest(plan, game, request))
                    Notify(error.Message);
                return;
            }
            if (!IsCurrentGameRequest(plan, game, request))
                return;
            plan.SourceProfiles[game.Id] = path;
        }
        else if (choice == folder)
        {
            var picker = new OpenFolderDialog { Title = "Choose the flight simulator settings folder", InitialDirectory = GameFolder(game) };
            if (picker.ShowDialog(this) != true)
                return;
            _plan.GameFolders[game.Id] = picker.FolderName;
            _plan.SourceProfiles.Remove(game.Id);
        }
        else return;
        await LoadGame();
    }
}
