using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace CncPrieniky3D;

public partial class SuflikKolikyDialog : Window
{
    public int PocetKolikov { get; private set; }

    public SuflikKolikyDialog(string sufelNazov, int currentPocet = 0)
    {
        InitializeComponent();
        InfoText.Text = sufelNazov;
        PocetKolikov = currentPocet > 0 ? currentPocet : 0;
        PocetBox.Text = PocetKolikov > 0 ? PocetKolikov.ToString(CultureInfo.InvariantCulture) : "";
        Loaded += (_, _) =>
        {
            PocetBox.Focus();
            PocetBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
        => TryAccept();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PocetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryAccept();
            e.Handled = true;
        }
    }

    private void TryAccept()
    {
        string text = (PocetBox.Text ?? "").Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && !int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out n))
        {
            MessageBox.Show(this, "Zadaj celé číslo (počet kolíkov).", "Počet kolíkov",
                MessageBoxButton.OK, MessageBoxImage.Information);
            PocetBox.Focus();
            PocetBox.SelectAll();
            return;
        }

        if (n < 0)
        {
            MessageBox.Show(this, "Počet kolíkov nemôže byť záporný.", "Počet kolíkov",
                MessageBoxButton.OK, MessageBoxImage.Information);
            PocetBox.Focus();
            PocetBox.SelectAll();
            return;
        }

        PocetKolikov = n;
        DialogResult = true;
        Close();
    }
}
