using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CncPrieniky3D.Models;

namespace CncPrieniky3D;

public partial class PartPropertiesWindow : Window
{
    private const string DefaultMsgB = "po ABS - odsadit o listu - otoc";

    private readonly DielecModel _diel;
    private string? _lastAutoAbs;
    private bool _suppressMsg;

    public bool OtocitSpodkomHore => FlipCheck.IsChecked == true;
    public bool AbsJeDole => AbsDoleRadio.IsChecked == true;
    public bool DruheUpnutie => DruheUpnutieCheck.IsChecked == true;
    public string XcsInfoMessage => (InfoMessageBox.Text ?? "").Trim();
    public string XcsInfoMessageB => (InfoMessageBBox.Text ?? "").Trim();
    public double AbsPaskaMm { get; private set; } = 0.8;

    public PartPropertiesWindow(DielecModel diel)
    {
        _diel = diel;
        InitializeComponent();
        NameText.Text = diel.Nazov;
        SizeText.Text = $"{diel.RozmerX:0.##} × {diel.RozmerY:0.##} × {diel.RozmerZ:0.##} mm";
        FlipCheck.IsChecked = diel.OtocitSpodkomHore;

        SetupAbsOrientUi(diel);
        AbsHoreRadio.IsChecked = !diel.AbsJeDole;
        AbsDoleRadio.IsChecked = diel.AbsJeDole;

        AbsPaskaPanel.Visibility = diel.MaAbs ? Visibility.Visible : Visibility.Collapsed;
        double paska = diel.AbsPaskaMm > 0 ? diel.AbsPaskaMm : 0.8;
        AbsPaskaBox.Text = paska.ToString("0.##", CultureInfo.CurrentCulture);

        DruheUpnutieCheck.IsChecked = diel.DruheUpnutie;
        string msgB = string.IsNullOrWhiteSpace(diel.XcsInfoMessageB)
            ? DefaultMsgB
            : diel.XcsInfoMessageB;
        InfoMessageBBox.Text = msgB;
        UpdateDruheUpnutiePanel();

        _suppressMsg = true;
        _lastAutoAbs = ComputeAutoAbs();
        string initial = !string.IsNullOrWhiteSpace(diel.XcsInfoMessage)
            ? diel.XcsInfoMessage
            : (_lastAutoAbs ?? "");
        InfoMessageBox.Text = initial;
        _suppressMsg = false;
        RefreshAbsPreview();
    }

    private void SetupAbsOrientUi(DielecModel diel)
    {
        if (diel.AbsYObeStrany)
        {
            AbsOrientPanel.Visibility = Visibility.Visible;
            AbsHoreDolePanel.Visibility = Visibility.Collapsed;
            AbsOrientHint.Text =
                $"ABS y1 aj y2 = 1 → v CreateMessage ide vlavo aj vpravo (bok {diel.BokStrana}). Hore/dole netreba.";
            return;
        }

        if (diel.AbsPotrebujeHoreDole)
        {
            AbsOrientPanel.Visibility = Visibility.Visible;
            AbsHoreDolePanel.Visibility = Visibility.Collapsed;
            bool y1 = diel.AbsY1 != 0;
            string auto = y1
                ? (diel.BokStrana == "L" ? "vpredu vpravo" : "vpredu vlavo")
                : (diel.BokStrana == "L" ? "vpredu vlavo" : "vpredu vpravo");
            AbsOrientHint.Text =
                $"Bok {diel.BokStrana}: {(y1 ? "y1 = spodok" : "y2 = vrch")} → CreateMessage: ABS {auto}.";
            return;
        }

        if (diel.JeBok && !diel.AbsYJednaStrana && !diel.AbsYObeStrany)
        {
            AbsOrientPanel.Visibility = Visibility.Visible;
            AbsHoreDolePanel.Visibility = Visibility.Collapsed;
            AbsOrientHint.Text = "ABS y1 aj y2 = 0 → žiadna bočná páska (len vpredu/vzadu podľa x1/x2).";
            return;
        }

        AbsOrientPanel.Visibility = Visibility.Collapsed;
    }

    private void DruheUpnutie_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDruheUpnutiePanel();
        RefreshAbsPreview();
    }

    private void UpdateDruheUpnutiePanel()
    {
        DruheUpnutiePanel.Visibility = DruheUpnutie ? Visibility.Visible : Visibility.Collapsed;
        if (DruheUpnutie && string.IsNullOrWhiteSpace(InfoMessageBBox.Text))
            InfoMessageBBox.Text = DefaultMsgB;
    }

    private void AbsMapping_Changed(object sender, RoutedEventArgs e)
    {
        string? nextAuto = ComputeAutoAbs();
        string cur = (InfoMessageBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cur) ||
            string.Equals(cur, _lastAutoAbs, StringComparison.Ordinal))
        {
            _suppressMsg = true;
            InfoMessageBox.Text = nextAuto ?? "";
            _suppressMsg = false;
        }
        _lastAutoAbs = nextAuto;
        RefreshAbsPreview();
    }

    private void InfoMessageBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressMsg) return;
        RefreshAbsPreview();
    }

    private string? ComputeAutoAbs()
    {
        bool prev = _diel.AbsJeDole;
        _diel.AbsJeDole = AbsJeDole;
        string? msg = _diel.BuildAbsMessageText();
        _diel.AbsJeDole = prev;
        return msg;
    }

    private void RefreshAbsPreview()
    {
        string text = (InfoMessageBox.Text ?? "").Trim();
        if (text.Length == 0)
            text = ComputeAutoAbs() ?? "";

        if (DruheUpnutie)
        {
            const string suffix = " - druhe up. po ABS";
            if (text.EndsWith(" -druhe up. po ABS", StringComparison.OrdinalIgnoreCase))
                text = text[..^" -druhe up. po ABS".Length].TrimEnd();
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                text = text.Length == 0 ? suffix.TrimStart() : text + suffix;
        }

        if (text.Length == 0)
        {
            AbsPreviewText.Text = "Bez poznámky (CreateMessage sa neexportuje).";
            return;
        }

        AbsPreviewText.Text = $"Náhľad XCS: CreateMessage(\"Info\",\"{text}\",…)";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string paskaTxt = (AbsPaskaBox.Text ?? "").Trim().Replace(',', '.');
        if (!double.TryParse(paskaTxt, NumberStyles.Float, CultureInfo.InvariantCulture, out double paska)
            || paska <= 0)
        {
            MessageBox.Show(this, "Zadaj platnú hrúbku ABS pásky (mm > 0).", "Vlastnosti",
                MessageBoxButton.OK, MessageBoxImage.Information);
            AbsPaskaBox.Focus();
            return;
        }

        AbsPaskaMm = paska;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
