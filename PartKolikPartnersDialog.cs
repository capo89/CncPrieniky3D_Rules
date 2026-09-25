using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CncPrieniky3D;

/// <summary>Výber partnerov (rolí) pri kolíkovaní podľa dielcov.</summary>
public class PartKolikPartnersDialog : Window
{
    public HashSet<string> SelectedRoles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PartKolikPartnersDialog(IEnumerable<string> availableRoles, string info)
    {
        Title = "Kolíkovať dielce – partneri";
        Width = 420;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x1A, 0x2E));
        Foreground = Brushes.WhiteSmoke;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var infoTb = new TextBlock
        {
            Text = info,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB8, 0xD8))
        };
        Grid.SetRow(infoTb, 0);
        root.Children.Add(infoTb);

        var panel = new StackPanel();
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var roleLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bokL"] = "Bok L",
            ["bokP"] = "Bok P",
            ["chrbat"] = "Chrbát",
            ["dno"] = "Dno",
            ["vrch"] = "Vrch",
            ["priecka"] = "Priecka",
            ["polica"] = "Polica",
            ["traverza"] = "Traverza",
            ["stojka"] = "Stojka",
        };

        var checks = new List<(string Role, CheckBox Box)>();
        foreach (var role in availableRoles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            string label = roleLabels.TryGetValue(role, out var pretty) ? pretty : role;
            var cb = new CheckBox
            {
                Content = label,
                Tag = role,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = Brushes.WhiteSmoke,
                IsChecked = role is "bokL" or "bokP"
            };
            if (role is not ("bokL" or "bokP"))
                cb.IsChecked = false;
            checks.Add((role, cb));
            panel.Children.Add(cb);
        }

        if (!checks.Any(c => c.Role is "bokL" or "bokP"))
        {
            foreach (var (_, cb) in checks)
                cb.IsChecked = true;
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var ok = new Button { Content = "Ďalej", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, Padding = new Thickness(8, 4, 8, 4) };
        var cancel = new Button { Content = "Zrušiť", Width = 90, IsCancel = true, Padding = new Thickness(8, 4, 8, 4) };
        ok.Click += (_, _) =>
        {
            SelectedRoles.Clear();
            foreach (var (role, cb) in checks)
            {
                if (cb.IsChecked == true)
                    SelectedRoles.Add(role);
            }
            if (SelectedRoles.Count == 0)
            {
                MessageBox.Show(this, "Zaškrtni aspoň jedného partnera.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }
}
