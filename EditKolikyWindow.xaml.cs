using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CncPrieniky3D.Models;

namespace CncPrieniky3D;

/// <summary>Editovateľný riadok série v dialógu.</summary>
internal sealed class KolikSerieEditRow : INotifyPropertyChanged
{
    private int _cislo;
    private double _odPredu;
    private int _pocet;
    private double _roztec;
    private bool _zDruhej;
    private bool _zoStredu;

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

    public bool ZoStredu
    {
        get => _zoStredu;
        set
        {
            if (_zoStredu == value) return;
            _zoStredu = value;
            OnPropertyChanged();
            if (_zoStredu)
                ZDruhejStrany = false;
        }
    }

    public bool ZDruhejStrany
    {
        get => _zDruhej;
        set
        {
            if (_zDruhej == value) return;
            _zDruhej = value;
            OnPropertyChanged();
            if (_zDruhej)
                ZoStredu = false;
        }
    }

    public static KolikSerieEditRow From(KolikSerie s) => new()
    {
        Cislo = s.Cislo,
        OdPredu = s.OdPredu,
        Pocet = s.PocetKolikov,
        Roztec = s.RoztecKolikov,
        ZoStredu = s.ZoStredu,
        ZDruhejStrany = s.ZDruhejStrany
    };

    public KolikSerie ToSerie() => new()
    {
        Cislo = Cislo,
        OdPredu = ZoStredu ? 0 : OdPredu,
        PocetKolikov = Pocet,
        RoztecKolikov = Roztec,
        ZoStredu = ZoStredu,
        ZDruhejStrany = !ZoStredu && ZDruhejStrany
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class EditKolikyWindow : Window
{
    private readonly List<ContactMark> _contacts;
    private readonly Dictionary<ContactMark, ObservableCollection<KolikSerieEditRow>> _byContact = new();

    public EditKolikyWindow(IReadOnlyList<ContactMark> contacts)
    {
        if (contacts.Count == 0)
            throw new ArgumentException("Potrebná aspoň jedna plocha.", nameof(contacts));

        _contacts = contacts.ToList();
        InitializeComponent();

        foreach (var c in _contacts)
        {
            var rows = new ObservableCollection<KolikSerieEditRow>();
            foreach (var s in c.KolikSerie)
                rows.Add(KolikSerieEditRow.From(s));
            _byContact[c] = rows;
        }

        ContactPicker.ItemsSource = _contacts;
        ContactPicker.SelectedIndex = 0;
        SerieGrid.ItemsSource = _byContact[_contacts[0]];

        if (_contacts.Count == 1)
        {
            PickerColumn.Width = new GridLength(0);
            PickerSplitterColumn.Width = new GridLength(0);
            ContactPickerPanel.Visibility = Visibility.Collapsed;
            TitleText.Text = $"{_contacts[0].LabelText} — {_contacts[0].PartA} ↔ {_contacts[0].PartB}";
            DetailText.Visibility = Visibility.Collapsed;
        }
        else
        {
            TitleText.Text = $"Upraviť kolíky — {_contacts.Count} plôch";
            UpdateDetailText(_contacts[0]);
        }

        SerieGrid.PreparingCellForEdit += SerieGrid_PreparingCellForEdit;
    }

    private ObservableCollection<KolikSerieEditRow> ActiveRows =>
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
        int nSer = c.KolikSerie.Count;
        int nKol = c.CelkovyPocetKolikov;
        DetailText.Text = $"{c.LabelText} — {c.PartA} ↔ {c.PartB} ({nSer} sér., {nKol}×)";
    }

    private static void SerieGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is TextBox tb)
            tb.Dispatcher.BeginInvoke(() => tb.SelectAll());
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var rows = ActiveRows;
        rows.Add(new KolikSerieEditRow
        {
            Cislo = rows.Count + 1,
            OdPredu = 30,
            Pocet = 3,
            Roztec = 128,
            ZoStredu = false,
            ZDruhejStrany = false
        });
        Renumber(rows);
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        var rows = ActiveRows;
        if (SerieGrid.SelectedItem is KolikSerieEditRow row)
            rows.Remove(row);
        else if (rows.Count > 0)
            rows.RemoveAt(rows.Count - 1);
        Renumber(rows);
    }

    private static void Renumber(ObservableCollection<KolikSerieEditRow> rows)
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
            foreach (var r in rows)
            {
                if (r.Pocet < 1 || r.Pocet > 100 || r.Roztec <= 0)
                {
                    MessageBox.Show(this,
                        $"„{c.LabelText}“, séria {r.Cislo}: skontroluj Počet (1–100) a Rozteč (>0).",
                        "Upraviť kolíky", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ContactPicker.SelectedItem = c;
                    SerieGrid.ItemsSource = rows;
                    return;
                }

                if (!r.ZoStredu && r.OdPredu < 0)
                {
                    MessageBox.Show(this,
                        $"„{c.LabelText}“, séria {r.Cislo}: Od predu musí byť ≥ 0 (alebo zapni Zo stredu).",
                        "Upraviť kolíky", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ContactPicker.SelectedItem = c;
                    SerieGrid.ItemsSource = rows;
                    return;
                }
            }

            c.ReplaceAllSerie(rows.Select(r => r.ToSerie()));
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
