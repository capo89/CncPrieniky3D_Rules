using System.Globalization;
using System.IO;
using System.Text;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Log používateľských vstupov do súboru vedľa otvoreného Excelu ({názov}_ui.log).
/// </summary>
internal static class UiInputLogger
{
    private static readonly object Lock = new();
    private static string? _excelPath;

    public static void SetExcelPath(string? path) => _excelPath = path;

    public static void Write(string action, string detail)
    {
        if (string.IsNullOrEmpty(_excelPath))
            return;

        try
        {
            string? dir = Path.GetDirectoryName(_excelPath);
            if (string.IsNullOrEmpty(dir))
                return;

            string logPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(_excelPath) + "_ui.log");
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {action} | {detail}{Environment.NewLine}";
            lock (Lock)
                File.AppendAllText(logPath, line, Encoding.UTF8);
        }
        catch
        {
            // log nesmie blokovať UI
        }
    }

    public static string FormatContact(ContactMark c) =>
        $"{c.LabelText} ({c.PartA} ↔ {c.PartB})";

    public static string FormatKolikSerie(KolikSerie s) =>
        $"#{s.Cislo} odPredu={s.OdPredu.ToString("0.##", CultureInfo.InvariantCulture)} " +
        $"pocet={s.PocetKolikov} roztec={s.RoztecKolikov.ToString("0.##", CultureInfo.InvariantCulture)} " +
        $"zDruhej={s.ZDruhejStrany} zoStredu={s.ZoStredu}";

    public static string FormatSkrutkaSerie(SkrutkaSerie s) =>
        $"#{s.Cislo} odPredu={s.OdPredu.ToString("0.##", CultureInfo.InvariantCulture)} " +
        $"pocet={s.PocetSkrutiek} roztec={s.RoztecSkrutiek.ToString("0.##", CultureInfo.InvariantCulture)} " +
        $"zDruhej={s.ZDruhejStrany} sym={s.Symetricke}" +
        (s.Symetricke ? (s.SymetriaMedziKolikmi ? "/koliky" : "/dotyk") : "");

    public static string FormatKolikSerieList(IEnumerable<KolikSerie> series) =>
        string.Join(" | ", series.Select(FormatKolikSerie));

    public static string FormatSkrutkaSerieList(IEnumerable<SkrutkaSerie> series) =>
        string.Join(" | ", series.Select(FormatSkrutkaSerie));
}
