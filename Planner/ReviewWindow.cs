using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace VisualControlsBinder.Planner;

internal static class ReviewWindow
{
    public static bool Confirm(Window owner, string title, string details, string action)
    {
        var text = new TextBox
        {
            Text = details, TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };
        AutomationProperties.SetName(text, title);
        return Show(owner, title, text, action, 640, 440);
    }

    public static T? Pick<T>(Window owner, string title, IReadOnlyList<T> choices, int width = 465) where T : class
    {
        var list = new ListBox { ItemsSource = choices, SelectedIndex = 0 };
        AutomationProperties.SetName(list, title);
        return Show(owner, title, list, "Select", width, 325) ? list.SelectedItem as T : null;
    }

    internal static bool Show(Window owner, string title, FrameworkElement content, string action, int width, int height)
    {
        var window = new Window
        {
            Owner = owner, Title = title, Width = width, Height = height, MinWidth = 400, MinHeight = 270,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = owner.FontFamily, FontSize = owner.FontSize, Background = owner.Background
        };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var header = new TextBlock
        {
            Text = title.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, FontSize = 19,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 13, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = action, IsDefault = true };
        confirm.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        confirm.Click += (_, _) => window.DialogResult = true;
        footer.Children.Add(cancel);
        footer.Children.Add(confirm);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(content);
        window.Content = layout;
        return window.ShowDialog() == true;
    }
}
