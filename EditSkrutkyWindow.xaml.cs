using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CncPrieniky3D.Models;
using CncPrieniky3D.Services;

namespace CncPrieniky3D;

internal sealed class SkrutkaSerieEditRow : INotifyPropertyChanged
{
    private int _cislo;
    private double _odPredu;
    private int _pocet;
    private double _roztec;
    private bool _zDruhej;
    private bool _symetricke;
    private bool _symetriaMedziKolikmi;

    public int Cislo
    {
        get => _cislo;
        set { if (_cislo == value) return; _cislo = value; OnPropertyChanged(); }
    }

    public double OdPredu
    {
        get => _odPredu;
        set { if (Math.Abs(_odPredu - value) < 1e-9) return; _odPredu = value; OnPropertyChanged(); }
    }

    public int Pocet
    {
        get => _pocet;
        set { if (_pocet == value) return; _pocet = value; OnPropertyChanged(); }
    }

    public double Roztec
    {
        get => _roztec;
        set { if (Math.Abs(_roztec - value) < 1e-9) return; _roztec = value; OnPropertyChanged(); }
    }

    public bool ZDruhejStrany
    {
        get => _zDruhej;
        set { if (_zDruhej == value) return; _zDruhej = value; OnPropertyChanged(); }
    }

    public bool Symetricke
    {
        get => _symetricke;
        set
        {
            if (_symetricke == value) return;
            _symetricke = value;
            if (_symetricke)
                ZDruhejStrany = false;
            else
                SymetriaMedziKolikmi = false;
            OnPropertyChanged();
        }
    }

    public bool SymetriaMedziKolikmi
    {
        get => _symetriaMedziKolikmi;
        set
        {
            if (_symetriaMedziKolikmi == value) return;
            _symetriaMedziKolikmi = value;
            if (_symetriaMedziKolikmi)
            {
                Symetricke = true;
                ZDruhejStrany = false;
            }
            OnPropertyChanged();
        }
    }

    public static SkrutkaSerieEditRow From(SkrutkaSerie s) => new()
    {
        Cislo = s.Cislo,
        OdPredu = s.OdPredu,
        Pocet = s.PocetSkrutiek,
        Roztec = s.RoztecSkrutiek,
        ZDruhejStrany = s.ZDruhejStrany,
        Symetricke = s.Symetricke,
        SymetriaMedziKolikmi = s.SymetriaMedziKolikmi
    };

    public SkrutkaSerie ToSerie() => new()
    {
        Cislo = Cislo,
        OdPredu = OdPredu,
        PocetSkrutiek = Pocet,
        RoztecSkrutiek = Roztec,
        ZDruhejStrany = ZDruhejStrany,
        Symetricke = Symetricke,
        SymetriaMedziKolikmi = Symetricke && SymetriaMedziKolikmi
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class EditSkrutkyWindow : Window
{
    private readonly List<ContactMark> _contacts;
    private readonly Dictionary<ContactMark, ObservableCollection<SkrutkaSerieEditRow>> _byContact = new();

    public EditSkrutkyWindow(IReadOnlyList<ContactMark> contacts)
    {
        if (contacts.Count == 0)
            throw new ArgumentException("Potrebná aspoň jedna plocha.", nameof(contacts));

        _contacts = contacts.ToList();
        InitializeComponent();

        foreach (var c in _contacts)
        {
            var rows = new ObservableCollection<SkrutkaSerieEditRow>();
            foreach (var s in c.SkrutkySerie)
                rows.Add(SkrutkaSerieEditRow.From(s));
            _byContact[c] = rows;
        }

        ContactPicker.ItemsSource = _contacts;
        ContactPicker.SelectedIndex = 0;
        SerieGrid.ItemsSource = _byContact[_contacts[0]];

        if (_contacts.Count == 1)
        {
            var c = _contacts[0];
            PickerColumn.Width = new GridLength(0);
            PickerSplitterColumn.Width = new GridLength(0);
            ContactPickerPanel.Visibility = Visibility.Collapsed;
            TitleText.Text = $"{c.LabelText} — {c.PartA} ↔ {c.PartB}";
            DetailText.Text =
                $"Dĺžka styčnej plochy: {SkrutkaLayout.PrimaryLength(c):0.##} mm " +
                "(symetrická rozteč = (L − 2·Od predu)/(n−1))";
        }
        else
        {
            TitleText.Text = $"Upraviť skrutky — {_contacts.Count} plôch";
            UpdateDetailText(_contacts[0]);
        }

        SerieGrid.PreparingCellForEdit += SerieGrid_PreparingCellForEdit;
    }

    private ObservableCollection<SkrutkaSerieEditRow> ActiveRows =>
        ContactPicker.SelectedItem is ContactMark c
            ? _byContact[c]
            : _byContact[_contacts[0]];

    private void ContactPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactPicker.SelectedItem is not ContactMark c)
            return;

        SerieGrid.ItemsSource = _byContact[c];
        if (_contacts.Count > 1)
            UpdateDetailText(c);
    }

    private void UpdateDetailText(ContactMark c)
    {
        double len = SkrutkaLayout.PrimaryLength(c);
        DetailText.Text =
            $"{c.LabelText} — {c.PartA} ↔ {c.PartB} | L={len:0.##} mm | " +
            $"{c.SkrutkySerie.Count} sér., {c.CelkovyPocetSkrutiek}×";
    }

    private static void SerieGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is TextBox tb)
            tb.Dispatcher.BeginInvoke(() => tb.SelectAll());
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var rows = ActiveRows;
        rows.Add(new SkrutkaSerieEditRow
        {
            Cislo = rows.Count + 1,
            OdPredu = 50,
            Pocet = 3,
            Roztec = 128,
            ZDruhejStrany = false,
            Symetricke = false,
            SymetriaMedziKolikmi = false
        });
        Renumber(rows);
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        var rows = ActiveRows;
        if (SerieGrid.SelectedItem is SkrutkaSerieEditRow row)
            rows.Remove(row);
        else if (rows.Count > 0)
            rows.RemoveAt(rows.Count - 1);
        Renumber(rows);
    }

    private static void Renumber(ObservableCollection<SkrutkaSerieEditRow> rows)
    {
        int i = 1;
        foreach (var r in rows)
            r.Cislo = i++;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SerieGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        SerieGrid.CommitEdit(DataGridEditingUnit.Row, true);

        foreach (var c in _contacts)
        {
            var rows = _byContact[c];
            var series = new List<SkrutkaSerie>();

            foreach (var r in rows)
            {
                if (r.OdPredu < 0 || r.Pocet < 1 || r.Pocet > 100)
                {
                    MessageBox.Show(this,
                        $"„{c.LabelText}“, séria {r.Cislo}: skontroluj Od predu (≥0), Počet (1–100).",
                        "Upraviť skrutky", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ContactPicker.SelectedItem = c;
                    SerieGrid.ItemsSource = rows;
                    return;
                }

                if (!r.Symetricke && r.Roztec <= 0)
                {
                    MessageBox.Show(this,
                        $"„{c.LabelText}“, séria {r.Cislo}: zadaj Rozteč (>0), alebo zapni Symetrické.",
                        "Upraviť skrutky", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ContactPicker.SelectedItem = c;
                    SerieGrid.ItemsSource = rows;
                    return;
                }

                var serie = r.ToSerie();
                if (!SkrutkaLayout.ApplySymetricIfNeeded(c, serie, out string? err))
                {
                    MessageBox.Show(this, $"„{c.LabelText}“, séria {r.Cislo}: {err}",
                        "Upraviť skrutky", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ContactPicker.SelectedItem = c;
                    SerieGrid.ItemsSource = rows;
                    return;
                }
                series.Add(serie);
            }

            c.ReplaceAllSkrutkySerie(series);
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
