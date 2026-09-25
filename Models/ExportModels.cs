using System.Text.RegularExpressions;
using CncPrieniky3D.Services;

namespace CncPrieniky3D.Models;

public sealed class ExportDocument
{
    public string BlockName { get; set; } = "";

    /// <summary>Cesta k zdrojovému Excelu (multi-skrinka workspace).</summary>
    public string ExcelPath { get; set; } = "";

    /// <summary>Stabilný kľúč skrinky (napr. skr2 / názov súboru).</summary>
    public string SkrinkaKey { get; set; } = "";

    /// <summary>Zobrazenie v taboch / strome (napr. „skrinka 2“).</summary>
    public string SkrinkaLabel { get; set; } = "";

    public List<DielecModel> Diely { get; } = new();
    public List<ContactMark> Dotyky { get; } = new();

    /// <summary>Varovania pri načítaní (napr. nezhoda AABB↔CNC).</summary>
    public List<string> LoadWarnings { get; } = new();

    public IEnumerable<DielecModel> KorpusDiely => Diely.Where(d => !d.JeSuflik);
    public IEnumerable<DielecModel> SuflikDiely => Diely.Where(d => d.JeSuflik);
}

public sealed class DielecModel
{
    public int Cislo { get; set; }
    public string Nazov { get; set; } = "";
    public string Vrstva { get; set; } = "";
    public string Handle { get; set; } = "";

    /// <summary>Kľúč skrinky (Excel), z ktorého dielec pochádza.</summary>
    public string SkrinkaKey { get; set; } = "";

    /// <summary>Popisok skrinky pre UI.</summary>
    public string SkrinkaLabel { get; set; } = "";

    public double RozmerX { get; set; }
    public double RozmerY { get; set; }
    public double RozmerZ { get; set; }

    /// <summary>CNC X z hárku „Povodny kusovnik“ (pôvodný kusovník). 0 = nie je.</summary>
    public double CncRozmerX { get; set; }

    /// <summary>CNC Y z hárku „Povodny kusovnik“.</summary>
    public double CncRozmerY { get; set; }

    /// <summary>CNC hrúbka z hárku „Povodny kusovnik“.</summary>
    public double CncHrubka { get; set; }

    /// <summary>True = má CNC rozmery z pôvodného kusovníka (na obrobok / ABS).</summary>
    public bool HasCncRozmery =>
        CncRozmerX > 0.1 && CncRozmerY > 0.1 && CncHrubka > 0.1;

    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MinZ { get; set; }

    public double WcsMinX { get; set; }
    public double WcsMinY { get; set; }
    public double WcsMinZ { get; set; }

    /// <summary>ABS páskovanie z Excelu (0/1): x1=vpredu, x2=vzadu; y1/y2 = bočné (pri bokoch hore/dole).</summary>
    public int AbsX1 { get; set; }
    public int AbsY1 { get; set; }
    public int AbsX2 { get; set; }
    public int AbsY2 { get; set; }

    /// <summary>Hrúbka ABS pásky (mm) pre makro Obeh_novy_DTD — default 0.8.</summary>
    public double AbsPaskaMm { get; set; } = 0.8;

    /// <summary>
    /// Pri bokoch s práve jednou ABS y-stranou: true = páska je dole (pohľad na skrinku),
    /// false = hore. Mapovanie do XCS: dole → bok L=vpravo, bok P=vlavo.
    /// </summary>
    public bool AbsJeDole { get; set; }

    /// <summary>
    /// Text CreateMessage (Info). Ak prázdny → auto z ABS.
    /// Používateľ môže dopísať / upraviť vo Vlastnostiach.
    /// </summary>
    public string XcsInfoMessage { get; set; } = "";

    /// <summary>Druhé upnutie → súbory _A a _B.</summary>
    public bool DruheUpnutie { get; set; }

    /// <summary>Poznámka pre súbor _B (druhé upnutie).</summary>
    public string XcsInfoMessageB { get; set; } = "po ABS - odsadit o listu - otoc";

    /// <summary>BREP vrcholy od Min rohu AABB (list Body dielca).</summary>
    public List<BodModel> Body { get; } = new();

    /// <summary>BREP plochy (list Plochy dielca) – legacy mesh.</summary>
    public List<PlochaBodModel> Plochy { get; } = new();

    /// <summary>Prerušenia bounding obdĺžnika (list Vyrezy dielca) – zdroj pravdy pre 2D/3D.</summary>
    public List<VyrezModel> Vyrezy { get; } = new();

    /// <summary>Otvory z listu Prieniky (Typ Diera / Diera hranata).</summary>
    public List<PrienikModel> Diery { get; } = new();

    /// <summary>CNC značenie na ploche dielca.</summary>
    public List<CncZnacenieModel> CncZnacenia { get; } = new();

    /// <summary>CNC: obrábať spodnú plochu (Bottom) — dielec otočený spodkom hore.</summary>
    public bool OtocitSpodkomHore { get; set; }

    /// <summary>Skrytý v 3D scéne (zoznam dielov zostáva).</summary>
    public bool JeSkryty { get; set; }

    /// <summary>Dielec zo hárku Sufle (nie korpus).</summary>
    public bool JeSuflik { get; set; }

    /// <summary>Riadok „pozicia“ = celý šufel (AABB inštancie).</summary>
    public bool JeSuflikPozicia { get; set; }

    /// <summary>Číslo inštancie šufľa (pozicia); 0 = agregát dielu.</summary>
    public int SufelCislo { get; set; }

    /// <summary>bok | celo | zad | dno | ine</summary>
    public string SufelTypDielu { get; set; } = "";

    /// <summary>vrchny | spodny | stredny</summary>
    public string SufelVarianta { get; set; } = "";

    /// <summary>Počet kusov pri agregáte zo Sufle.</summary>
    public int PocetKusov { get; set; } = 1;

    /// <summary>Počet kolíkov pre inštanciu šufľa (pozicia) — nastaví používateľ.</summary>
    public int PocetKolikov { get; set; }

    public bool MaAbs => AbsX1 != 0 || AbsY1 != 0 || AbsX2 != 0 || AbsY2 != 0;

    /// <summary>Práve jedna z ABS y1/y2 (treba hore/dole pri bokoch).</summary>
    public bool AbsYJednaStrana => (AbsY1 != 0) ^ (AbsY2 != 0);

    /// <summary>Obe ABS y strany — vlavo aj vpravo, bez voľby.</summary>
    public bool AbsYObeStrany => AbsY1 != 0 && AbsY2 != 0;

    /// <summary>„L“ / „P“ / "" podľa názvu (bok).</summary>
    public string BokStrana
    {
        get
        {
            string nazov = Nazov ?? "";
            string n = nazov.ToLowerInvariant();
            if (!n.Contains("bok")) return "";
            if (System.Text.RegularExpressions.Regex.IsMatch(nazov, @"\bP\b")
                || n.EndsWith(" p") || n.EndsWith("_p") || n.EndsWith("-p"))
                return "P";
            if (System.Text.RegularExpressions.Regex.IsMatch(nazov, @"\bL\b")
                || n.EndsWith(" l") || n.EndsWith("_l") || n.EndsWith("-l"))
                return "L";
            return "";
        }
    }

    public bool JeBok => BokStrana is "L" or "P";

    /// <summary>Voľba hore/dole je relevantná (bok + práve jedna y-strana).</summary>
    public bool AbsPotrebujeHoreDole => JeBok && AbsYJednaStrana;

    /// <summary>
    /// ABS strany pre makro Obeh — priamo z Excel AbsX1/Y1/X2/Y2 (nie z textu poznámky).
    /// Korpus: x1=vpredu, x2=vzadu; y1/y2 = vlavo/vpravo (pri bokoch podľa L/P a hore/dole).
    /// </summary>
    public void GetAbsSideFlags(
        out bool vpredu, out bool vlavo, out bool vpravo, out bool vzadu)
    {
        vpredu = vlavo = vpravo = vzadu = false;
        if (!MaAbs) return;

        bool isSuflik = JeSuflik && !JeSuflikPozicia;

        // Šufel: AbsX1 = dlhá hrana vzadu. Korpus: AbsX1 = vpredu.
        if (AbsX1 != 0)
        {
            if (isSuflik) vzadu = true;
            else vpredu = true;
        }

        if (AbsYObeStrany)
        {
            vlavo = true;
            vpravo = true;
        }
        else if (AbsYJednaStrana)
        {
            if (JeBok && !isSuflik)
            {
                // Korpus bok: y1 = spodok → L: vpravo, P: vlavo; y2 = vrch → opak
                bool jeDole = AbsY1 != 0;
                if (jeDole)
                {
                    if (BokStrana == "L") vpravo = true;
                    else vlavo = true;
                }
                else
                {
                    if (BokStrana == "L") vlavo = true;
                    else vpravo = true;
                }
            }
            else
            {
                if (AbsY1 != 0) vlavo = true;
                if (AbsY2 != 0) vpravo = true;
            }
        }

        // Korpus: AbsX2 = vzadu. Šufel: AbsX2 → vpredu.
        if (AbsX2 != 0)
        {
            if (isSuflik) vpredu = true;
            else vzadu = true;
        }
    }

    /// <summary>Text CreateMessage bez úvodzoviek, napr. „1ks - ABS vpredu vlavo“. Null ak žiadne ABS.</summary>
    public string? BuildAbsMessageText(int kusov = 1)
    {
        if (!MaAbs) return null;
        if (kusov < 1) kusov = 1;

        GetAbsSideFlags(out bool vpredu, out bool vlavo, out bool vpravo, out bool vzadu);

        var sides = new List<string>();
        if (vpredu) sides.Add("vpredu");
        if (vlavo) sides.Add("vlavo");
        if (vpravo) sides.Add("vpravo");
        if (vzadu) sides.Add("vzadu");

        if (sides.Count == 0)
            return null;
        // Všetky 4 hrany → skrátene „ABS 4x“.
        if (sides.Count == 4)
            return $"{kusov}ks - ABS 4x";
        return $"{kusov}ks - ABS " + string.Join(" ", sides);
    }

    /// <summary>Finálny text Info správy: ručný (XcsInfoMessage) alebo auto ABS.</summary>
    public string? ResolveInfoMessage(int kusov = 1)
    {
        string custom = (XcsInfoMessage ?? "").Trim();
        if (custom.Length > 0)
        {
            if (kusov <= 1) return custom;
            // „1ks - …“ → „Nks - …“
            if (Regex.IsMatch(custom, @"^\d+\s*ks\b", RegexOptions.IgnoreCase))
                return Regex.Replace(custom, @"^\d+\s*ks\b", $"{kusov}ks", RegexOptions.IgnoreCase);
            return $"{kusov}ks - {custom}";
        }
        return BuildAbsMessageText(kusov);
    }

    public override string ToString()
    {
        string displayNazov = PartRules.CncDisplayName(this, PocetKusov);
        string s = $"{Cislo}. {displayNazov}  ({RozmerX:0.#}×{RozmerY:0.#}×{RozmerZ:0.#})";
        // Prefix skrinky len pri multi-workspace (SkrinkaLabel nastaví UI).
        if (!string.IsNullOrEmpty(SkrinkaLabel))
            s = $"[{SkrinkaLabel}] {s}";
        if (JeSuflik)
        {
            if (JeSuflikPozicia)
            {
                s = $"{Cislo}. {Nazov}  ({RozmerX:0.#}×{RozmerY:0.#}×{RozmerZ:0.#})  [šufel]";
                if (!string.IsNullOrEmpty(SkrinkaLabel))
                    s = $"[{SkrinkaLabel}] {s}";
            }
            else
            {
                s += "  [šufel";
                if (!string.IsNullOrEmpty(SufelTypDielu)) s += $" {SufelTypDielu}";
                if (PocetKusov > 1) s += $" ×{PocetKusov}";
                s += "]";
            }
            if (JeSuflikPozicia && PocetKolikov > 0)
                s += $"  {PocetKolikov}× kolík";
        }
        else if (PocetKusov > 1)
        {
            s += $"  ×{PocetKusov} ks";
        }
        if (OtocitSpodkomHore) s += "  [spodkom hore]";
        if (JeSkryty) s += "  [skrytý]";
        return s;
    }
}

public sealed class BodModel
{
    public int Cislo { get; set; }
    public double PosX { get; set; }
    public double PosY { get; set; }
    public double PosZ { get; set; }
}

/// <summary>Bod BREP plochy (obrys / diera) z listu Plochy dielca.</summary>
public sealed class PlochaBodModel
{
    public int PlochaCislo { get; set; }
    public int BodCislo { get; set; }
    /// <summary>obrys | diera</summary>
    public string Typ { get; set; } = "obrys";
    public double PosX { get; set; }
    public double PosY { get; set; }
    public double PosZ { get; set; }
}

/// <summary>Prerušenie bounding obdĺžnika z listu Vyrezy dielca.</summary>
public sealed class VyrezModel
{
    public int Cislo { get; set; }
    /// <summary>diera | vyrez_hranovy</summary>
    public string Typ { get; set; } = "";
    /// <summary>kruh | hranaty | polygon</summary>
    public string Tvar { get; set; } = "";
    public double PosX { get; set; }
    public double PosY { get; set; }
    public double PosZ { get; set; }
    public double Priemer { get; set; }
    public double Sirka { get; set; }
    public double Vyska { get; set; }
    public double Hlbka { get; set; }
    public string Poznamka { get; set; } = "";

    /// <summary>Body polygónu (face X/Y), prázdne pri kruh/hranaty s 1 riadkom.</summary>
    public List<BodModel> Body { get; } = new();

    public bool JeDiera => Typ.StartsWith("diera", StringComparison.OrdinalIgnoreCase);
    public bool JeHranovy => Typ.Contains("hranov", StringComparison.OrdinalIgnoreCase)
                             || Typ.Contains("vyrez", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Otvor v diele z Excelu (Prieniky) — kruh alebo hranatý.</summary>
public sealed class PrienikModel
{
    public int Cislo { get; set; }
    /// <summary>"Diera" | "Diera hranata".</summary>
    public string Typ { get; set; } = "";
    public double PosX { get; set; }
    public double PosY { get; set; }
    public double PosZ { get; set; }
    public double Priemer { get; set; }
    public double Sirka { get; set; }
    public double Vyska { get; set; }
    /// <summary>Dĺžka / hĺbka z Excelu (ak je).</summary>
    public double Hlbka { get; set; }

    public bool JeDiera => Typ.StartsWith("Diera", StringComparison.OrdinalIgnoreCase);
}

public sealed class CncZnacenieModel
{
    public int Cislo { get; set; }
    /// <summary>movento / magnet / nohy / zaves… z Excelu alebo vrstvy.</summary>
    public string Typ { get; set; } = "";
    public double PosX { get; set; }
    public double PosY { get; set; }
    public double PosZ { get; set; }
    public double Priemer { get; set; }
    /// <summary>Dĺžka / hĺbka značky (mm) — pri podperkách na „kovanie“ typicky 12.</summary>
    public double Hlbka { get; set; }
    public string Vrstva { get; set; } = "";
    public string Handle { get; set; } = "";

    /// <summary>Normalizovaný typ (malé písmená). Ak Typ prázdny → z vrstvy.</summary>
    public string ResolvedTyp => CncZnacenieTyp.Resolve(Typ, Vrstva, Priemer, Hlbka);
}

/// <summary>Jednoznačné určenie typu CNC značenia z Excelu / vrstvy.</summary>
internal static class CncZnacenieTyp
{
    public const string Movento = "movento";
    public const string Magnet = "magnet";
    public const string Nohy = "nohy";
    public const string Zaves = "zaves";
    /// <summary>Závesy System32 v rade (skr2): pár Ø ~32 mm naprieč, viac kusov pozdĺž výšky.</summary>
    public const string ZavesRady = "zaves_rady";
    public const string Podperky = "podperky";

    /// <summary>Podperka na hladine kovanie: Ø 3 × hĺbka 12 mm.</summary>
    public const double PodperkyPriemerMm = 3.0;
    public const double PodperkyHlbkaMm = 12.0;
    public const double PodperkySizeTolMm = 1.0; // CAD často ~12.37, nie presne 12

    public static bool MatchesPodperkyGeometry(double priemer, double hlbka)
        => Math.Abs(priemer - PodperkyPriemerMm) <= PodperkySizeTolMm
           && Math.Abs(hlbka - PodperkyHlbkaMm) <= PodperkySizeTolMm;

    public static string Resolve(string? typ, string? vrstva, double priemer = 0, double hlbka = 0)
    {
        // Typ Kovanie / hladina kovanie|spotrebiče → podperky len pri Ø3 × hĺbka 12.
        if (IsPodperkyHardwareLayer(vrstva)
            || string.Equals((typ ?? "").Trim(), "Kovanie", StringComparison.OrdinalIgnoreCase)
            || string.Equals((typ ?? "").Trim(), "kovanie", StringComparison.OrdinalIgnoreCase))
        {
            return MatchesPodperkyGeometry(priemer, hlbka) ? Podperky : "";
        }

        string t = (typ ?? "").Trim();
        string resolved = t.Length > 0 ? NormalizeToken(t) : FromLayer(vrstva);
        if (string.IsNullOrEmpty(resolved) || resolved is "znaceniecnc")
        {
            string fromLayer = FromLayer(vrstva);
            if (!string.IsNullOrEmpty(fromLayer))
                resolved = fromLayer;
        }
        if (string.Equals(resolved, "znaceniecnc", StringComparison.OrdinalIgnoreCase))
            return Podperky;
        if (string.Equals(resolved, "kovanie", StringComparison.OrdinalIgnoreCase))
            return MatchesPodperkyGeometry(priemer, hlbka) ? Podperky : "";
        return resolved;
    }

    public static bool IsMovento(CncZnacenieModel z)
        => string.Equals(z.ResolvedTyp, Movento, StringComparison.OrdinalIgnoreCase);

    public static bool IsNohy(CncZnacenieModel z)
        => string.Equals(z.ResolvedTyp, Nohy, StringComparison.OrdinalIgnoreCase)
           || (z.ResolvedTyp ?? "").Contains("nohy", StringComparison.OrdinalIgnoreCase);

    public static bool IsZaves(CncZnacenieModel z)
        => string.Equals(z.ResolvedTyp, Zaves, StringComparison.OrdinalIgnoreCase)
           || ((z.ResolvedTyp ?? "").Contains("zaves", StringComparison.OrdinalIgnoreCase)
               && !IsZavesRady(z));

    /// <summary>Závesy v rade (System32 pár + rozteč po výške) — iný pattern než klasické zavesy.</summary>
    public static bool IsZavesRady(CncZnacenieModel z)
        => string.Equals(z.ResolvedTyp, ZavesRady, StringComparison.OrdinalIgnoreCase)
           || (z.ResolvedTyp ?? "").Contains("zaves_rady", StringComparison.OrdinalIgnoreCase)
           || (z.ResolvedTyp ?? "").Contains("zavesrady", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ľubovoľné závesy (klasické aj rady) — 3D filter hranových dier.</summary>
    public static bool IsAnyZaves(CncZnacenieModel z) => IsZaves(z) || IsZavesRady(z);

    public static bool IsPodperky(CncZnacenieModel z)
    {
        // kovanie / spotrebiče / Typ Kovanie: striktne Ø3 × 12
        if (IsPodperkyHardwareLayer(z.Vrstva)
            || string.Equals(z.Typ, "Kovanie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(z.Typ, "kovanie", StringComparison.OrdinalIgnoreCase))
            return MatchesPodperkyGeometry(z.Priemer, z.Hlbka);

        return string.Equals(z.ResolvedTyp, Podperky, StringComparison.OrdinalIgnoreCase)
               || (z.ResolvedTyp ?? "").Contains("podperk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Hladiny, kde sa podperky môžu vyskytnúť (rozhoduje geometria Ø3×12).</summary>
    public static bool IsPodperkyHardwareLayer(string? layerName)
        => IsKovanieLayer(layerName) || IsSpotrebiceLayer(layerName);

    /// <summary>True ak názov vrstvy je „kovanie“.</summary>
    public static bool IsKovanieLayer(string? layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName)) return false;
        string n = NormalizeLayer(layerName);
        return n == "kovanie" || n.EndsWith("kovanie", StringComparison.Ordinal);
    }

    /// <summary>
    /// Holé „znacenie CNC“ / „cnc znacenie“ bez prípony (_zaves, _nohy, …).
    /// Exporter to mapuje na podperky — závesy System32 treba reklasifikovať v appke.
    /// </summary>
    public static bool IsPlainZnacenieCncLayer(string? layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName)) return false;
        string n = NormalizeLayer(layerName);
        return n is "znaceniecnc" or "cncznacenie";
    }

    /// <summary>True ak názov vrstvy je „spotrebič(e)“ / „spotrebic“.</summary>
    public static bool IsSpotrebiceLayer(string? layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName)) return false;
        string n = NormalizeLayer(layerName);
        return n.Contains("spotrebic", StringComparison.Ordinal);
    }

    /// <summary>„znacenie CNC movento“ → movento; samotné „kovanie“/„spotrebiče“ nie je typ (rozhoduje geometria).</summary>
    public static string FromLayer(string? layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName))
            return "";

        string n = NormalizeLayer(layerName);

        // kovanie / spotrebiče samo o sebe ≠ podperky — treba Ø3 × hĺbka 12.
        if (n == "kovanie" || n.EndsWith("kovanie", StringComparison.Ordinal)
            || n.Contains("spotrebic", StringComparison.Ordinal))
            return "";

        foreach (string key in new[] { "znaceniecnc", "cncznacenie" })
        {
            int idx = n.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) continue;
            string suffix = n[(idx + key.Length)..];
            return string.IsNullOrEmpty(suffix) ? Podperky : NormalizeToken(suffix);
        }

        if (n.Contains("movento", StringComparison.Ordinal)) return Movento;
        if (n.Contains("magnet", StringComparison.Ordinal)) return Magnet;
        if (n.Contains("podperk", StringComparison.Ordinal)) return Podperky;
        if (n.Contains("nohy", StringComparison.Ordinal)) return Nohy;
        if (n.Contains("zaves", StringComparison.Ordinal)) return Zaves;
        return "";
    }

    private static string NormalizeLayer(string layerName)
    {
        string s = layerName.Trim().ToLowerInvariant();
        s = s.Replace("á", "a").Replace("ä", "a").Replace("č", "c").Replace("ď", "d")
            .Replace("é", "e").Replace("í", "i").Replace("ľ", "l").Replace("ĺ", "l")
            .Replace("ň", "n").Replace("ó", "o").Replace("ô", "o").Replace("ŕ", "r")
            .Replace("š", "s").Replace("ť", "t").Replace("ú", "u").Replace("ý", "y")
            .Replace("ž", "z");
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    private static string NormalizeToken(string t)
    {
        t = t.Trim().ToLowerInvariant();
        // „kovanie“ ako typ bez rozmerov — nie podperky
        if (t is "kovanie")
            return "";
        // Excel „Znacenie CNC“ / vrstva bez prípony → podperky
        if (t is "znacenie cnc" or "znaceniecnc" or "cnc")
            return Podperky;
        t = new string(t.Where(ch => ch is not (' ' or '_' or '-')).ToArray());
        if (t is "kovanie")
            return "";
        if (t is "znaceniecnc" or "cncznacenie")
            return Podperky;
        return t;
    }
}
