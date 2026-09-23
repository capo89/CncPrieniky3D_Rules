using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CncPrieniky3D.Models;

namespace CncPrieniky3D;

public partial class ColorsSettingsWindow : Window
{
    private readonly List<(string Key, string Label, Func<ViewColors, Color> Get, Action<ViewColors, Color> Set)> _defs;
    private readonly Dictionary<string, Color> _draft = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _swatches = new(StringComparer.Ordinal);

    public ColorsSettingsWindow()
    {
        InitializeComponent();
        _defs =
        [
            ("WindowBackground", "Pozadie okna", c => c.WindowBackground, (c, v) => c.WindowBackground = v),
            ("ToolbarBackground", "Pozadie lišty", c => c.ToolbarBackground, (c, v) => c.ToolbarBackground = v),
            ("ViewportBackground", "Pozadie 3D scény", c => c.ViewportBackground, (c, v) => c.ViewportBackground = v),
            ("Panel", "Farba dielcov", c => c.Panel, (c, v) => c.Panel = v),
            ("PanelSelected", "Vybraný dielec", c => c.PanelSelected, (c, v) => c.PanelSelected = v),
            ("Vertex", "Body dielca", c => c.Vertex, (c, v) => c.Vertex = v),
            ("Koliky", "Kolíky", c => c.Koliky, (c, v) => c.Koliky = v),
            ("Skrutky", "Skrutky", c => c.Skrutky, (c, v) => c.Skrutky = v),
            ("CncZnacenie", "CNC značenie (skrinka)", c => c.CncZnacenie, (c, v) => c.CncZnacenie = v),
            ("CncZnaceniePreview", "CNC značenie (celá skrinka)", c => c.CncZnaceniePreview, (c, v) => c.CncZnaceniePreview = v),
            ("Diery", "Diery / vŕtania", c => c.Diery, (c, v) => c.Diery = v),
            ("Abs", "ABS hrany", c => c.Abs, (c, v) => c.Abs = v),
            ("Dotyky", "Dotyky", c => c.Dotyky, (c, v) => c.Dotyky = v),
            ("DotykOznaceny", "Dotyk označený", c => c.DotykOznaceny, (c, v) => c.DotykOznaceny = v),
            ("DotykVybrany", "Dotyk vybraný", c => c.DotykVybrany, (c, v) => c.DotykVybrany = v),
        ];

        var src = ViewColors.Current;
        foreach (var d in _defs)
            _draft[d.Key] = d.Get(src);

        BuildRows();
    }

    private void BuildRows()
    {
        RowsPanel.Children.Clear();
        _swatches.Clear();

        foreach (var d in _defs)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = d.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(label, 0);

            var swatch = new Border
            {
                Width = 56,
                Height = 28,
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Klikni a zvoľ farbu",
                Background = new SolidColorBrush(_draft[d.Key])
            };
            Grid.SetColumn(swatch, 1);
            string key = d.Key;
            swatch.MouseLeftButtonUp += (_, _) => PickColor(key);

            row.Children.Add(label);
            row.Children.Add(swatch);
            RowsPanel.Children.Add(row);
            _swatches[key] = swatch;
        }
    }

    private void PickColor(string key)
    {
        var dlg = new ColorPickDialog(_draft[key]) { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        _draft[key] = dlg.SelectedColor;
        _swatches[key].Background = new SolidColorBrush(dlg.SelectedColor);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var def = new ViewColors();
        foreach (var d in _defs)
        {
            _draft[d.Key] = d.Get(def);
            _swatches[d.Key].Background = new SolidColorBrush(_draft[d.Key]);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var cur = ViewColors.Current;
        foreach (var d in _defs)
            d.Set(cur, _draft[d.Key]);
        cur.Save();
        DialogResult = true;
        Close();
    }
}
