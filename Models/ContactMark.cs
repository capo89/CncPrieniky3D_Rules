using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Media3D;

namespace CncPrieniky3D.Models;

/// <summary>Styčná plocha diel–diel, dá sa označiť, kolíkovať a skrutkovať.</summary>
public sealed class ContactMark : INotifyPropertyChanged
{
    private string _oznacenie = "";
    private bool _oznaceny;

    public int Cislo { get; set; }
    public string PartA { get; init; } = "";
    public string PartB { get; init; } = "";
    public Point3D Center { get; init; }
    public Vector3D Size { get; init; }
    public int Axis { get; init; }

    /// <summary>Automatický styk šufľa (bok↔čelo/zad) — v zozname Dotyky sa nezobrazuje.</summary>
    public bool JeSuflikAuto { get; set; }

    public List<KolikSerie> KolikSerie { get; } = new();
    public List<SkrutkaSerie> SkrutkySerie { get; } = new();

    public bool MaKoliky => KolikSerie.Count > 0;
    public bool MaSkrutky => SkrutkySerie.Count > 0;

    public int CelkovyPocetKolikov => KolikSerie.Sum(s => s.PocetKolikov);
    public int CelkovyPocetSkrutiek => SkrutkySerie.Sum(s => s.PocetSkrutiek);

    public string Oznacenie
    {
        get => _oznacenie;
        set
        {
            if (_oznacenie == value) return;
            _oznacenie = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
            if (!string.IsNullOrWhiteSpace(_oznacenie))
                Oznaceny = true;
        }
    }

    public bool Oznaceny
    {
        get => _oznaceny;
        set
        {
            if (_oznaceny == value) return;
            _oznaceny = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public string Display
    {
        get
        {
            string baseTxt = Oznaceny && !string.IsNullOrWhiteSpace(Oznacenie)
                ? $"{Cislo}. [{Oznacenie}]  {PartA} ↔ {PartB}"
                : $"{Cislo}. {PartA} ↔ {PartB}";
            if (MaKoliky)
                baseTxt += $"  ·  {KolikSerie.Count} sér./{CelkovyPocetKolikov}× kolík";
            if (MaSkrutky)
                baseTxt += $"  ·  {SkrutkySerie.Count} sér./{CelkovyPocetSkrutiek}× skrutka";
            return baseTxt;
        }
    }

    public string LabelText =>
        !string.IsNullOrWhiteSpace(Oznacenie)
            ? Oznacenie
            : $"D{Cislo}";

    public void AddSerie(KolikSerie serie)
    {
        serie.Cislo = KolikSerie.Count + 1;
        KolikSerie.Add(serie);
        NotifyKolikyChanged();
    }

    public void ReplaceAllSerie(IEnumerable<KolikSerie> series)
    {
        KolikSerie.Clear();
        int n = 1;
        foreach (var s in series)
        {
            s.Cislo = n++;
            KolikSerie.Add(s);
        }
        NotifyKolikyChanged();
    }

    public void AddSkrutkaSerie(SkrutkaSerie serie)
    {
        serie.Cislo = SkrutkySerie.Count + 1;
        SkrutkySerie.Add(serie);
        NotifySkrutkyChanged();
    }

    public void ReplaceAllSkrutkySerie(IEnumerable<SkrutkaSerie> series)
    {
        SkrutkySerie.Clear();
        int n = 1;
        foreach (var s in series)
        {
            s.Cislo = n++;
            SkrutkySerie.Add(s);
        }
        NotifySkrutkyChanged();
    }

    private void NotifyKolikyChanged()
    {
        OnPropertyChanged(nameof(MaKoliky));
        OnPropertyChanged(nameof(CelkovyPocetKolikov));
        OnPropertyChanged(nameof(Display));
    }

    private void NotifySkrutkyChanged()
    {
        OnPropertyChanged(nameof(MaSkrutky));
        OnPropertyChanged(nameof(CelkovyPocetSkrutiek));
        OnPropertyChanged(nameof(Display));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
