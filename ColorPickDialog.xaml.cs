using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CncPrieniky3D.Models;

namespace CncPrieniky3D;

public partial class ColorPickDialog : Window
{
    private bool _suppress;

    public Color SelectedColor { get; private set; }

    public ColorPickDialog(Color initial)
    {
        InitializeComponent();
        SelectedColor = initial;
        _suppress = true;
        SliderR.Value = initial.R;
        SliderG.Value = initial.G;
        SliderB.Value = initial.B;
        _suppress = false;
        SyncFromSliders();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        SyncFromSliders();
    }

    private void SyncFromSliders()
    {
        byte r = (byte)SliderR.Value;
        byte g = (byte)SliderG.Value;
        byte b = (byte)SliderB.Value;
        SelectedColor = Color.FromRgb(r, g, b);
        Preview.Background = new SolidColorBrush(SelectedColor);
        LabelR.Text = r.ToString();
        LabelG.Text = g.ToString();
        LabelB.Text = b.ToString();
        HexBox.Text = ViewColors.ToHex(SelectedColor).TrimStart('#');
    }

    private void ApplyHex()
    {
        if (!ViewColors.TryParse(HexBox.Text, out var c))
            return;
        _suppress = true;
        SliderR.Value = c.R;
        SliderG.Value = c.G;
        SliderB.Value = c.B;
        _suppress = false;
        SyncFromSliders();
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ApplyHex();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ApplyHex();
        DialogResult = true;
        Close();
    }
}
