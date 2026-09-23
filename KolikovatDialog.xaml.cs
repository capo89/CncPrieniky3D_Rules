using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CncPrieniky3D.Models;

namespace CncPrieniky3D;

public partial class KolikovatDialog : Window
{
    /// <summary>Jedna alebo dve série (pri „Aj z druhej strany“).</summary>
    public IReadOnlyList<KolikSerie> SerieList { get; private set; } = Array.Empty<KolikSerie>();

    /// <summary>Prvá séria (spätná kompatibilita).</summary>
    public KolikSerie Serie => SerieList.Count > 0 ? SerieList[0] : new();

    public KolikovatDialog(string plochaInfo, double odPredu = 30, int pocet = 3, double roztec = 128, bool zDruhej = false)
    {
        InitializeComponent();
        InfoText.Text = plochaInfo;
        OdPreduBox.Text = odPredu.ToString("0.##", CultureInfo.CurrentCulture);
        PocetBox.Text = pocet.ToString(CultureInfo.CurrentCulture);
        RoztecBox.Text = roztec.ToString("0.##", CultureInfo.CurrentCulture);
        OdPredu2Box.Text = odPredu.ToString("0.##", CultureInfo.CurrentCulture);
        Pocet2Box.Text = pocet.ToString(CultureInfo.CurrentCulture);
        Roztec2Box.Text = roztec.ToString("0.##", CultureInfo.CurrentCulture);
        ZDruhejStranyBox.IsChecked = zDruhej;
        UpdateModeUi();
        Loaded += (_, _) =>
        {
            if (ZoStreduBox.IsChecked == true)
                PocetBox.Focus();
            else
                OdPreduBox.Focus();
        };
    }

    private void ZoStreduBox_Changed(object sender, RoutedEventArgs e) => UpdateModeUi();

    private void ZDruhejStranyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ZDruhejStranyBox.IsChecked == true)
        {
            // Predvyplň 2. sériu hodnotami z 1. — môžeš ich potom upraviť
            OdPredu2Box.Text = OdPreduBox.Text;
            Pocet2Box.Text = PocetBox.Text;
            Roztec2Box.Text = RoztecBox.Text;
        }
        UpdateModeUi();
    }

    private void UpdateModeUi()
    {
        bool zoStredu = ZoStreduBox.IsChecked == true;
        OdPreduBox.IsEnabled = !zoStredu;
        OdPreduLabel.Opacity = zoStredu ? 0.45 : 1;
        ZDruhejStranyBox.IsEnabled = !zoStredu;
        if (zoStredu)
            ZDruhejStranyBox.IsChecked = false;

        bool both = !zoStredu && ZDruhejStranyBox.IsChecked == true;
        SecondSeriePanel.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = both ? "Pridať 2 série" : "Pridať sériu";
        Height = both ? 520 : 420;
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
        bool zoStredu = ZoStreduBox.IsChecked == true;
        double odPredu = 0;

        if (!zoStredu)
        {
            if (!TryDouble(OdPreduBox.Text, out odPredu) || odPredu < 0)
            {
                MessageBox.Show(this, "Zadaj platné „Od predu“ (mm ≥ 0) pre 1. sériu.", "Kolíkovať",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                OdPreduBox.Focus();
                return false;
            }
        }

        if (!TryInt(PocetBox.Text, out int pocet) || pocet < 1 || pocet > 100)
        {
            MessageBox.Show(this, "Zadaj počet kolíkov (1–100) pre 1. sériu.", "Kolíkovať",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PocetBox.Focus();
            return false;
        }

        if (!TryDouble(RoztecBox.Text, out double roztec) || roztec <= 0)
        {
            MessageBox.Show(this, "Zadaj platnú rozteč (mm > 0) pre 1. sériu.", "Kolíkovať",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            RoztecBox.Focus();
            return false;
        }

        var list = new List<KolikSerie>
        {
            new()
            {
                OdPredu = odPredu,
                PocetKolikov = pocet,
                RoztecKolikov = roztec,
                ZoStredu = zoStredu,
                ZDruhejStrany = false
            }
        };

        bool ajZDruhej = !zoStredu && ZDruhejStranyBox.IsChecked == true;
        if (ajZDruhej)
        {
            if (!TryDouble(OdPredu2Box.Text, out double odPredu2) || odPredu2 < 0)
            {
                MessageBox.Show(this, "Zadaj platné „Od predu“ (mm ≥ 0) pre 2. sériu.", "Kolíkovať",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                OdPredu2Box.Focus();
                return false;
            }

            if (!TryInt(Pocet2Box.Text, out int pocet2) || pocet2 < 1 || pocet2 > 100)
            {
                MessageBox.Show(this, "Zadaj počet kolíkov (1–100) pre 2. sériu.", "Kolíkovať",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Pocet2Box.Focus();
                return false;
            }

            if (!TryDouble(Roztec2Box.Text, out double roztec2) || roztec2 <= 0)
            {
                MessageBox.Show(this, "Zadaj platnú rozteč (mm > 0) pre 2. sériu.", "Kolíkovať",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Roztec2Box.Focus();
                return false;
            }

            list.Add(new KolikSerie
            {
                OdPredu = odPredu2,
                PocetKolikov = pocet2,
                RoztecKolikov = roztec2,
                ZoStredu = false,
                ZDruhejStrany = true
            });
        }

        SerieList = list;
        return true;
    }

    private static bool TryInt(string? text, out int value)
    {
        text = text?.Trim() ?? "";
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
            return true;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryDouble(string? text, out double value)
    {
        text = text?.Trim() ?? "";
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return true;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
