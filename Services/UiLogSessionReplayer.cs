using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Obnovenie kolíkov/skrutiek z *_ui.log (append denník), keď chýba *.cnc3d.json.
/// </summary>
internal static class UiLogSessionReplayer
{
    private static readonly Regex LineRx = new(
        @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\s+\|\s+(?<action>\w+)\s+\|\s+(?<detail>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ContactRx = new(
        @"(?<label>.+?) \((?<a>[^↔]+) ↔ (?<b>[^)]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex KolikRx = new(
        @"#\d+\s+odPredu=(?<od>[-\d.]+)\s+pocet=(?<n>\d+)\s+roztec=(?<r>[-\d.]+)\s+zDruhej=(?<zd>\w+)\s+zoStredu=(?<zs>\w+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SkrutkaRx = new(
        @"#\d+\s+odPredu=(?<od>[-\d.]+)\s+pocet=(?<n>\d+)\s+roztec=(?<r>[-\d.]+)\s+zDruhej=(?<zd>\w+)\s+sym=(?<sym>\w+)(?<mode>/koliky|/dotyk)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string LogPathForExcel(string excelPath)
    {
        string? dir = Path.GetDirectoryName(excelPath);
        string name = Path.GetFileNameWithoutExtension(excelPath);
        return Path.Combine(dir ?? ".", name + "_ui.log");
    }

    public static bool ExistsForExcel(string excelPath)
        => File.Exists(LogPathForExcel(excelPath));

    /// <summary>Prehrá akcie z logu. Vracia počet úspešne zasiahnutých plôch.</summary>
    public static int ApplyFromExcel(ExportDocument doc, string excelPath)
    {
        string path = LogPathForExcel(excelPath);
        if (!File.Exists(path)) return 0;
        return Apply(doc, File.ReadAllLines(path));
    }

    public static int Apply(ExportDocument doc, IEnumerable<string> lines)
    {
        int hits = 0;
        foreach (string raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var m = LineRx.Match(raw.Trim());
            if (!m.Success) continue;

            string action = m.Groups["action"].Value;
            string detail = m.Groups["detail"].Value;
            hits += action switch
            {
                "Kolikovat" => ReplayKolikovat(doc, detail),
                "PasteKoliky" => ReplayPasteKoliky(doc, detail),
                "EditKoliky" => ReplayEditKoliky(doc, detail),
                "Skrutky" => ReplaySkrutky(doc, detail),
                "EditSkrutky" => ReplayEditSkrutky(doc, detail),
                _ => 0
            };
        }

        return hits;
    }

    private static int ReplayKolikovat(ExportDocument doc, string detail)
    {
        if (!TrySplitCieleAndRest(detail, out var contacts, out string rest))
            return 0;
        var series = ParseKolikSeries(rest);
        if (series.Count == 0) return 0;

        int n = 0;
        foreach (var c in ResolveContacts(doc, contacts))
        {
            Mark(c);
            foreach (var s in series)
                c.AddSerie(CloneKolik(s));
            n++;
        }
        return n;
    }

    private static int ReplayPasteKoliky(ExportDocument doc, string detail)
    {
        if (!TrySplitCieleAndRest(detail, out var contacts, out string rest))
            return 0;
        var series = ParseKolikSeries(rest);
        if (series.Count == 0) return 0;

        int n = 0;
        foreach (var c in ResolveContacts(doc, contacts))
        {
            Mark(c);
            c.ReplaceAllSerie(series.Select(CloneKolik));
            n++;
        }
        return n;
    }

    private static int ReplayEditKoliky(ExportDocument doc, string detail)
    {
        int n = 0;
        foreach (string chunk in detail.Split(" || ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TrySplitContactAndRest(chunk, out var key, out string rest))
                continue;
            var c = FindContact(doc, key);
            if (c == null) continue;

            if (rest.Contains("(odstránené)", StringComparison.OrdinalIgnoreCase))
            {
                c.ReplaceAllSerie(Array.Empty<KolikSerie>());
                n++;
                continue;
            }

            var series = ParseKolikSeries(rest);
            Mark(c);
            c.ReplaceAllSerie(series.Select(CloneKolik));
            n++;
        }
        return n;
    }

    private static int ReplaySkrutky(ExportDocument doc, string detail)
    {
        if (!TrySplitCieleAndRest(detail, out var contacts, out string rest))
            return 0;
        var series = ParseSkrutkaSeries(rest);
        if (series.Count == 0) return 0;

        var targets = ResolveContacts(doc, contacts).ToList();
        if (targets.Count == 0) return 0;

        // Log často opakuje rovnakú sériu pre každú plochu (Last() per target).
        bool allSame = series.Count > 1 && series.Skip(1).All(s => SameSkrutka(s, series[0]));
        int n = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            var c = targets[i];
            Mark(c);
            SkrutkaSerie src = allSame || series.Count == 1
                ? series[0]
                : series[Math.Min(i, series.Count - 1)];
            var clone = CloneSkrutka(src);
            if (!SkrutkaLayout.ApplySymetricIfNeeded(c, clone, out _))
                continue;
            c.AddSkrutkaSerie(clone);
            n++;
        }
        return n;
    }

    private static int ReplayEditSkrutky(ExportDocument doc, string detail)
    {
        int n = 0;
        foreach (string chunk in detail.Split(" || ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TrySplitContactAndRest(chunk, out var key, out string rest))
                continue;
            var c = FindContact(doc, key);
            if (c == null) continue;

            if (rest.Contains("(odstránené)", StringComparison.OrdinalIgnoreCase))
            {
                c.ReplaceAllSkrutkySerie(Array.Empty<SkrutkaSerie>());
                n++;
                continue;
            }

            var series = ParseSkrutkaSeries(rest)
                .Select(CloneSkrutka)
                .Where(s => SkrutkaLayout.ApplySymetricIfNeeded(c, s, out _))
                .ToList();
            Mark(c);
            c.ReplaceAllSkrutkySerie(series);
            n++;
        }
        return n;
    }

    private static bool TrySplitCieleAndRest(string detail, out List<(string Label, string A, string B)> contacts, out string rest)
    {
        contacts = new();
        rest = "";
        int start = detail.IndexOf("ciele=[", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        start += "ciele=[".Length;
        int end = detail.IndexOf(']', start);
        if (end < 0) return false;

        string inside = detail[start..end];
        contacts = ParseContactList(inside);
        rest = detail[(end + 1)..].Trim();
        if (rest.StartsWith('|'))
            rest = rest[1..].Trim();
        return contacts.Count > 0;
    }

    private static bool TrySplitContactAndRest(string chunk, out (string Label, string A, string B) key, out string rest)
    {
        key = default;
        rest = "";
        var m = ContactRx.Match(chunk);
        if (!m.Success) return false;
        key = (
            m.Groups["label"].Value.Trim(),
            m.Groups["a"].Value.Trim(),
            m.Groups["b"].Value.Trim());
        rest = chunk[m.Length..].Trim();
        if (rest.StartsWith('|'))
            rest = rest[1..].Trim();
        return true;
    }

    private static List<(string Label, string A, string B)> ParseContactList(string inside)
    {
        var list = new List<(string, string, string)>();
        foreach (Match m in ContactRx.Matches(inside))
        {
            list.Add((
                m.Groups["label"].Value.Trim(),
                m.Groups["a"].Value.Trim(),
                m.Groups["b"].Value.Trim()));
        }
        return list;
    }

    private static List<KolikSerie> ParseKolikSeries(string text)
    {
        var list = new List<KolikSerie>();
        foreach (Match m in KolikRx.Matches(text))
        {
            list.Add(new KolikSerie
            {
                OdPredu = ParseD(m.Groups["od"].Value),
                PocetKolikov = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture),
                RoztecKolikov = ParseD(m.Groups["r"].Value),
                ZDruhejStrany = ParseBool(m.Groups["zd"].Value),
                ZoStredu = ParseBool(m.Groups["zs"].Value)
            });
        }
        return list;
    }

    private static List<SkrutkaSerie> ParseSkrutkaSeries(string text)
    {
        var list = new List<SkrutkaSerie>();
        foreach (Match m in SkrutkaRx.Matches(text))
        {
            bool sym = ParseBool(m.Groups["sym"].Value);
            string mode = m.Groups["mode"].Success ? m.Groups["mode"].Value : "";
            list.Add(new SkrutkaSerie
            {
                OdPredu = ParseD(m.Groups["od"].Value),
                PocetSkrutiek = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture),
                RoztecSkrutiek = ParseD(m.Groups["r"].Value),
                ZDruhejStrany = ParseBool(m.Groups["zd"].Value),
                Symetricke = sym,
                SymetriaMedziKolikmi = mode.Equals("/koliky", StringComparison.OrdinalIgnoreCase)
            });
        }
        return list;
    }

    private static IEnumerable<ContactMark> ResolveContacts(
        ExportDocument doc, List<(string Label, string A, string B)> keys)
    {
        foreach (var key in keys)
        {
            var c = FindContact(doc, key);
            if (c != null) yield return c;
        }
    }

    private static ContactMark? FindContact(ExportDocument doc, (string Label, string A, string B) key)
    {
        static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        var match = doc.Dotyky.FirstOrDefault(c =>
            !c.JeSuflikAuto
            && ((Same(c.PartA, key.A) && Same(c.PartB, key.B))
                || (Same(c.PartA, key.B) && Same(c.PartB, key.A))));
        if (match != null) return match;

        // Fallback podľa označenia (D1 / používateľský text)
        if (!string.IsNullOrWhiteSpace(key.Label))
        {
            return doc.Dotyky.FirstOrDefault(c =>
                !c.JeSuflikAuto
                && (Same(c.LabelText, key.Label) || Same(c.Oznacenie, key.Label)
                    || Same($"D{c.Cislo}", key.Label)));
        }

        return null;
    }

    private static void Mark(ContactMark c)
    {
        if (!c.Oznaceny)
        {
            if (string.IsNullOrWhiteSpace(c.Oznacenie))
                c.Oznacenie = c.LabelText;
            c.Oznaceny = true;
        }
    }

    private static KolikSerie CloneKolik(KolikSerie s) => new()
    {
        OdPredu = s.OdPredu,
        PocetKolikov = s.PocetKolikov,
        RoztecKolikov = s.RoztecKolikov,
        ZDruhejStrany = s.ZDruhejStrany,
        ZoStredu = s.ZoStredu
    };

    private static SkrutkaSerie CloneSkrutka(SkrutkaSerie s) => new()
    {
        OdPredu = s.OdPredu,
        PocetSkrutiek = s.PocetSkrutiek,
        RoztecSkrutiek = s.RoztecSkrutiek,
        ZDruhejStrany = s.ZDruhejStrany,
        Symetricke = s.Symetricke,
        SymetriaMedziKolikmi = s.SymetriaMedziKolikmi
    };

    private static bool SameSkrutka(SkrutkaSerie a, SkrutkaSerie b) =>
        a.OdPredu == b.OdPredu
        && a.PocetSkrutiek == b.PocetSkrutiek
        && a.RoztecSkrutiek == b.RoztecSkrutiek
        && a.ZDruhejStrany == b.ZDruhejStrany
        && a.Symetricke == b.Symetricke
        && a.SymetriaMedziKolikmi == b.SymetriaMedziKolikmi;

    private static double ParseD(string s) =>
        double.Parse(s, CultureInfo.InvariantCulture);

    private static bool ParseBool(string s) =>
        bool.TryParse(s, out bool b) && b;
}
