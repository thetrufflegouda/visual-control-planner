using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VisualControlsBinder.Hardware;

namespace VisualControlsBinder.Planner;

internal sealed class DeviceLibraryTile(HardwareDefinition layout) : INotifyPropertyChanged
{
    public HardwareDefinition Layout { get; } = layout;
    public FrameworkElement? Preview { get; private set; }
    public string Manufacturer => Layout.Manufacturer ?? "Custom controllers";
    public string Caption => Layout.Name.ToUpperInvariant();
    public event PropertyChangedEventHandler? PropertyChanged;

    public void LoadPreview(Func<HardwareDefinition, FrameworkElement> createPreview)
    {
        Preview = createPreview(Layout);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
    }
}

internal partial class DeviceLibraryWindow : Window
{
    private readonly IReadOnlyList<DeviceLibraryTile> _tiles;
    private readonly OwnedDeviceStore _ownedDevices;
    private readonly Func<HardwareDefinition, FrameworkElement> _createPreview;
    private HashSet<string> _owned = new(StringComparer.Ordinal);
    private bool _refreshingManufacturers;
    private bool _previewRefreshPending;

    private bool ShowingOwned => ReferenceEquals(LibraryTabs.SelectedItem, MyDevicesTab);

    public HardwareDefinition? SelectedLayout => (DeviceGrid.SelectedItem as DeviceLibraryTile)?.Layout;

    public DeviceLibraryWindow(IReadOnlyList<DeviceLibraryTile> tiles, OwnedDeviceStore ownedDevices,
        Func<HardwareDefinition, FrameworkElement> createPreview)
    {
        _tiles = tiles;
        _ownedDevices = ownedDevices;
        _createPreview = createPreview;
        InitializeComponent();

        try { _owned = _ownedDevices.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LibraryStatus.Text = $"Could not read My Devices. {error.Message}";
        }
        RefreshManufacturers(keepFilter: false);
    }

    private IEnumerable<DeviceLibraryTile> TabTiles() => ShowingOwned
        ? _tiles.Where(tile => _owned.Contains(tile.Layout.Id))
        : _tiles;

    private void SelectLibraryTab(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, LibraryTabs) && EmptyMyDevices is not null)
            RefreshManufacturers(keepFilter: false);
    }

    private void RefreshManufacturers(bool keepFilter)
    {
        var current = keepFilter ? Manufacturers.SelectedItem as string : null;
        var filters = new[] { "All manufacturers" }.Concat(TabTiles().Select(tile => tile.Manufacturer)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)).ToArray();
        _refreshingManufacturers = true;
        try
        {
            Manufacturers.ItemsSource = filters;
            Manufacturers.SelectedIndex = Math.Max(0, Array.IndexOf(filters, current));
        }
        finally { _refreshingManufacturers = false; }
        RefreshTiles();
    }

    private void FilterByManufacturer(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingManufacturers && ReferenceEquals(e.Source, Manufacturers) && EmptyMyDevices is not null)
            RefreshTiles();
    }

    private void RefreshTiles()
    {
        var selected = DeviceGrid.SelectedItem as DeviceLibraryTile;
        var visible = TabTiles().Where(tile => Manufacturers.SelectedIndex <= 0 ||
            string.Equals(tile.Manufacturer, Manufacturers.SelectedItem as string, StringComparison.OrdinalIgnoreCase)).ToArray();
        DeviceGrid.ItemsSource = visible;
        DeviceGrid.SelectedItem = selected is not null && visible.Contains(selected) ? selected : null;
        EmptyMyDevices.Visibility = ShowingOwned && visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if ((DeviceGrid.SelectedItem ?? visible.FirstOrDefault()) is { } first)
            DeviceGrid.ScrollIntoView(first);
        RefreshActions();
        QueuePreviews();
    }

    private void PreviewVisibilityChanged(object sender, RoutedEventArgs e) => QueuePreviews();

    private void QueuePreviews()
    {
        if (_previewRefreshPending || !DeviceGrid.IsLoaded)
            return;
        _previewRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)LoadVisiblePreviews);
    }

    private void LoadVisiblePreviews()
    {
        _previewRefreshPending = false;
        if (!DeviceGrid.IsLoaded)
            return;

        var viewport = new Rect(DeviceGrid.RenderSize);
        for (int i = 0; i < DeviceGrid.Items.Count; i++)
        {
            if (DeviceGrid.Items[i] is not DeviceLibraryTile { Preview: null } tile ||
                DeviceGrid.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item ||
                !DeviceGrid.IsAncestorOf(item))
                continue;
            Rect bounds = item.TransformToAncestor(DeviceGrid).TransformBounds(new Rect(item.RenderSize));
            if (viewport.IntersectsWith(bounds))
                tile.LoadPreview(_createPreview);
        }
    }

    private void SelectDevice(object sender, SelectionChangedEventArgs e) => RefreshActions();

    private void RefreshActions()
    {
        if (AddButton is null || OwnedDeviceButton is null)
            return;
        var layout = SelectedLayout;
        AddButton.IsEnabled = OwnedDeviceButton.IsEnabled = layout is not null;
        OwnedDeviceButton.Content = layout is not null && _owned.Contains(layout.Id)
            ? "Remove from My Devices" : "Add to My Devices";
    }

    private void ToggleOwnedDevice(object sender, RoutedEventArgs e)
    {
        if (SelectedLayout is not { } layout)
            return;

        bool add = !_owned.Contains(layout.Id);
        try { _owned = _ownedDevices.SetOwned(layout.Id, add); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LibraryStatus.Text = $"Could not save My Devices. {error.Message}";
            return;
        }

        LibraryStatus.Text = add ? "Added to My Devices." : "Removed from My Devices.";
        if (ShowingOwned)
            RefreshManufacturers(keepFilter: true);
        else
            RefreshActions();
    }

    private void AddDevice(object sender, RoutedEventArgs e)
    {
        if (SelectedLayout is not null)
            DialogResult = true;
    }

    private void OpenTile(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(DeviceGrid, source) is ListBoxItem { IsSelected: true })
        {
            e.Handled = true;
            DialogResult = true;
        }
    }
}
