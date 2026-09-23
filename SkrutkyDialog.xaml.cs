using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CncPrieniky3D.Models;

namespace CncPrieniky3D;

public partial class SkrutkyDialog : Window
{
    public SkrutkaSerie Serie { get; private set; } = new();

    public SkrutkyDialog(string plochaInfo, double odPredu = 50, int pocet = 3, double roztec = 128, bool zDruhej = false)
    {
        InitializeComponent();
        InfoText.Text = plochaInfo;
        OdPreduBox.Text = odPredu.ToString("0.##", CultureInfo.CurrentCulture);
        PocetBox.Text = pocet.ToString(CultureInfo.CurrentCulture);
        RoztecBox.Text = roztec.ToString("0.##", CultureInfo.CurrentCulture);
        ZDruhejStranyBox.IsChecked = zDruhej;
        UpdateSymetricUi();
        Loaded += (_, _) => OdPreduBox.Focus();
    }

    private void Symetricke_Changed(object sender, RoutedEventArgs e) => UpdateSymetricUi();
    private void SymMode_Changed(object sender, RoutedEventArgs e) => UpdateSymetricUi();

    private void UpdateSymetricUi()
    {
        // Počas InitializeComponent ešte nemusia byť všetky polia (RadioButton Checked).
        if (SymetrickeBox == null || RoztecBox == null || OdPreduLabel == null
            || SymModePanel == null || SymKolikyRadio == null || ZDruhejStranyBox == null
            || RoztecLabel == null)
            return;

        bool sym = SymetrickeBox.IsChecked == true;
        RoztecBox.IsEnabled = !sym;
        RoztecLabel.Opacity = sym ? 0.45 : 1;
        ZDruhejStranyBox.IsEnabled = !sym;
        SymModePanel.Visibility = sym ? Visibility.Visible : Visibility.Collapsed;
        if (sym)
            ZDruhejStranyBox.IsChecked = false;

        bool medzi = sym && SymKolikyRadio.IsChecked == true;
        OdPreduLabel.Text = medzi ? "Od 1. kolíka (mm)" : "Od predu (mm)";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParse())
            return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Ok_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Field_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.Dispatcher.BeginInvoke(() => tb.SelectAll());
    }

    private void Field_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            tb.Focus();
        }
    }

    private bool TryParse()
    {
        bool sym = SymetrickeBox.IsChecked == true;
        bool medziKolikmi = sym && SymKolikyRadio.IsChecked == true;

        if (!TryDouble(OdPreduBox.Text, out double odPredu) || odPredu < 0)
        {
            MessageBox.Show(this,
                medziKolikmi
                    ? "Zadaj platné „Od 1. kolíka“ (mm ≥ 0)."
                    : "Zadaj platné „Od predu“ (mm ≥ 0).",
                "Skrutky", MessageBoxButton.OK, MessageBoxImage.Warning);
            OdPreduBox.Focus();
            return false;
        }

        if (!int.TryParse(PocetBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int pocet)
            && !int.TryParse(PocetBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pocet)
            || pocet < 1 || pocet > 100)
        {
            MessageBox.Show(this, "Zadaj počet skrutiek (1–100).", "Skrutky",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PocetBox.Focus();
            return false;
        }

        double roztec = 0;
        if (!sym)
        {
            if (!TryDouble(RoztecBox.Text, out roztec) || roztec <= 0)
            {
                MessageBox.Show(this, "Zadaj platnú rozteč (mm > 0).", "Skrutky",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                RoztecBox.Focus();
                return false;
            }
        }

        Serie = new SkrutkaSerie
        {
            OdPredu = odPredu,
            PocetSkrutiek = pocet,
            RoztecSkrutiek = roztec,
            ZDruhejStrany = !sym && ZDruhejStranyBox.IsChecked == true,
            Symetricke = sym,
            SymetriaMedziKolikmi = medziKolikmi
        };
        return true;
    }

    private static bool TryDouble(string? text, out double value)
    {
        text = text?.Trim() ?? "";
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return true;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
