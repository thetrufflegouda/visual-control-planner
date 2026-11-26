using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using VisualControlsBinder.Games;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private readonly HashSet<(string Game, string Category)> _expandedActionGroups = [];
    private (string Game, string Category)? _revealedActionGroup;
    private bool _restoringActionGroup;

    private void ActionGroupLoaded(object sender, RoutedEventArgs e) => RestoreActionGroup((Expander)sender);

    private void ActionGroupContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Expander { IsLoaded: true } expander)
            RestoreActionGroup(expander);
    }

    private void RestoreActionGroup(Expander expander)
    {
        if (SelectedGame is not { } game || expander.DataContext is not CollectionViewGroup { Name: string category })
            return;

        _restoringActionGroup = true;
        try
        {
            expander.IsExpanded = !string.IsNullOrWhiteSpace(ActionSearch.Text)
                || _expandedActionGroups.Contains((game.Id, category))
                || _revealedActionGroup == (game.Id, category);
        }
        finally
        {
            _restoringActionGroup = false;
        }
        ClearSelectionIfHidden(expander, category);
    }

    private void ActionGroupToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { IsLoaded: true } expander || e.OriginalSource != expander
            || _restoringActionGroup || SelectedGame is not { } game
            || expander.DataContext is not CollectionViewGroup { Name: string category })
            return;

        if (string.IsNullOrWhiteSpace(ActionSearch.Text))
        {
            if (expander.IsExpanded)
                _expandedActionGroups.Add((game.Id, category));
            else
            {
                _expandedActionGroups.Remove((game.Id, category));
                if (_revealedActionGroup == (game.Id, category))
                    _revealedActionGroup = null;
            }
        }

        ClearSelectionIfHidden(expander, category);
    }

    private void ClearSelectionIfHidden(Expander expander, string category)
    {
        if (!expander.IsExpanded && ActionPicker.SelectedItem is ActionRow row && row.Group == category)
            ActionPicker.SelectedItem = null;
    }

    private void ActionGroupHeaderDoubleClicked(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ScrollAssignedActionIntoView(ActionRow row)
    {
        if (!ReferenceEquals(ActionPicker.SelectedItem, row))
            return;

        ActionPicker.UpdateLayout();
        ActionPicker.ScrollIntoView(row);
        if (ActionPicker.ItemContainerGenerator.ContainerFromItem(row) is not null ||
            ActionPicker.Items.Groups?.OfType<CollectionViewGroup>().FirstOrDefault(group => Equals(group.Name, row.Group))
                is not { } target ||
            ActionPicker.ItemContainerGenerator.ContainerFromItem(target) is not GroupItem groupItem)
            return;

        // The first scroll can realize an offscreen group without showing its children.
        groupItem.ApplyTemplate();
        groupItem.BringIntoView();
        ActionPicker.UpdateLayout();
        if (ReferenceEquals(ActionPicker.SelectedItem, row))
            ActionPicker.ScrollIntoView(row);
    }

    private bool _selectAssignedAction;
    private GameAction? _assignedAction;

    private void RevealAssignedAction(GameAction? requested = null)
    {
        if (requested is null)
        {
            var rows = (AssignmentPicker.ItemsSource as IEnumerable<AssignmentRow>)?.ToArray() ?? [];
            requested = rows.LastOrDefault(row => row.Edit is not null)?.Action ?? rows.FirstOrDefault()?.Action;
        }
        _assignedAction = requested is null ? null : _actions.FirstOrDefault(action => SameAction(action, requested));
        if (_assignedAction is { } action && !MatchesActionSearch(action, ActionSearch.Text.Trim()))
            ActionSearch.Text = "";
        _revealedActionGroup = _assignedAction is { } assigned && SelectedGame is { } game
            ? (game.Id, new ActionRow(assigned).Group) : null;
        _selectAssignedAction = true;
    }

    private static bool SameAction(GameAction left, GameAction right) => left.Map == right.Map && left.Id == right.Id;

    private static bool MatchesActionSearch(GameAction action, string search) =>
        action.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        action.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        action.Section.Contains(search, StringComparison.OrdinalIgnoreCase);
}
