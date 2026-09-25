using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Základ: veľkosť obrobku a rozpoznanie typu dielca.
/// Orientácie vŕtania dno↔bok sú v XcsProgramGenerator (režim A/B).
/// </summary>
internal abstract class PartRules
{
    /// <summary>Max. Y obrobku na CNC — otočenie X↔Y len kvôli tomuto limitu.</summary>
    public const double MaxWorkpieceYMm = 1350.0;

    public static PartRules For(DielecModel diel) => FallbackRules.Instance;

    /// <summary>
    /// Z = hrúbka. Ak sú CNC rozmery z „Povodny kusovnik“ → tie (X/Y/hrúbka).
    /// Inak AABB plochy v poradí osí (nie väčšie=X).
    /// Otočenie X↔Y len ak Y &gt; 1350 a po otočení Y ≤ 1350.
    /// </summary>
    public virtual (double Dx, double Dy, double Dz) WorkpieceSize(DielecModel diel)
    {
        if (diel.HasCncRozmery)
        {
            double dx = diel.CncRozmerX;
            double dy = diel.CncRozmerY;
            double dz = diel.CncHrubka;
            if (ShouldSwapForMaxY(dx, dy))
                return (dy, dx, dz);
            return (dx, dy, dz);
        }

        GetFaceDimsInAabbOrder(diel, out double a, out double b, out double thick);
        if (ShouldSwapForMaxY(a, b))
            return (b, a, thick);
        return (a, b, thick);
    }

    /// <summary>
    /// True = Face X (1. plošná os AABB) treba mapovať na workpiece Y
    /// (CNC orientácia / otočenie kvôli 1350).
    /// </summary>
    public static bool WorkpieceSwapsFaceAxes(DielecModel diel)
    {
        GetFaceDimsInAabbOrder(diel, out double faceA, out double faceB, out _);
        var (dx, dy, _) = For(diel).WorkpieceSize(diel);
        // Ktorý workpiece rozmer je bližší k 1. plošnej osi AABB
        return Math.Abs(faceA - dx) > Math.Abs(faceA - dy);
    }

    private static bool ShouldSwapForMaxY(double faceA, double faceB)
        => faceB > MaxWorkpieceYMm && faceA <= MaxWorkpieceYMm;

    /// <summary>
    /// Plošné osi AABB (bez najtenšej): faceA = 1. os, faceB = 2. os (ako Excel Pos X/Y).
    /// </summary>
    public static void GetFaceDimsInAabbOrder(
        DielecModel diel, out double faceA, out double faceB, out double thick)
    {
        double[] d = { diel.RozmerX, diel.RozmerY, diel.RozmerZ };
        int thin = 0;
        if (d[1] < d[0]) thin = 1;
        if (d[2] < d[thin]) thin = 2;

        thick = d[thin];
        faceA = -1;
        faceB = -1;
        for (int i = 0; i < 3; i++)
        {
            if (i == thin) continue;
            if (faceA < 0) faceA = d[i];
            else faceB = d[i];
        }
    }

    /// <summary>
    /// True = značka mimo plochy AABB (hranové diery závesov, typicky PosX≈−4.5).
    /// </summary>
    public static bool IsOutsideAabbFace(
        DielecModel diel, double faceX, double faceY, double tolMm = 2.0)
    {
        GetFaceDimsInAabbOrder(diel, out double faceA, out double faceB, out _);
        return OutsideSpan(faceX, faceA, tolMm) || OutsideSpan(faceY, faceB, tolMm);
    }

    private static bool OutsideSpan(double v, double size, double tolMm)
    {
        if (v < -tolMm) return true;
        if (v > size + tolMm) return true;
        return false;
    }

    /// <summary>Hrúbka na CNC: CNC hrúbka ak je, inak najtenší AABB.</summary>
    public static double ThinDimension(DielecModel d)
    {
        if (d.HasCncRozmery)
            return d.CncHrubka;
        return Math.Min(d.RozmerX, Math.Min(d.RozmerY, d.RozmerZ));
    }

    /// <summary>AABB vs CNC (zoradené rozmery), tolerancia mm.</summary>
    public static bool AabbMatchesCnc(DielecModel d, double tolMm = 1.0)
    {
        if (!d.HasCncRozmery) return true;
        var a = new[] { d.RozmerX, d.RozmerY, d.RozmerZ };
        var c = new[] { d.CncRozmerX, d.CncRozmerY, d.CncHrubka };
        Array.Sort(a);
        Array.Sort(c);
        return Math.Abs(a[0] - c[0]) <= tolMm
            && Math.Abs(a[1] - c[1]) <= tolMm
            && Math.Abs(a[2] - c[2]) <= tolMm;
    }

    /// <summary>
    /// Rola z názvu. Pri „priečka, vrch“ (obe v názve) → other — rozhodne Z.
    /// </summary>
    public static string DetectRole(string nazov)
    {
        string n = StripDiacritics(nazov);
        if (n.Contains("chrbat") || n.Contains("chrat")) return "chrbat";
        if (n.Contains("polic")) return "polica";
        if (n.Contains("stojk")) return "stojka";
        if (n.Contains("traverz") || n.Contains("traversa")) return "traverza";
        if (n.Contains("bok") && (n.Contains(" p") || n.EndsWith("p") || Regex.IsMatch(nazov, @"\bP\b")))
            return "bokP";
        if (n.Contains("bok") && (n.Contains(" l") || n.EndsWith("l") || Regex.IsMatch(nazov, @"\bL\b")))
            return "bokL";
        if (n.Contains("bok")) return "bokL";

        bool hasDno = n.Contains("dno");
        bool hasVrch = n.Contains("vrch");
        bool hasPrieck = n.Contains("prieck");
        // CAD často: „priečka, vrch“ na dne aj vrchu — nechaj Z rozhodnúť.
        if (hasPrieck && (hasVrch || hasDno))
            return "other";
        if (hasDno) return "dno";
        if (hasVrch) return "vrch";
        if (hasPrieck) return "priecka";
        return "other";
    }

    /// <summary>
    /// Rola dielca: bok L/P podľa vonkajšej plochy (WCS Y); vodorovné podľa Z;
    /// ostatné z názvu.
    /// </summary>
    public static string DetectRole(DielecModel diel, ExportDocument? doc = null)
    {
        string nameRole = DetectRole(diel.Nazov);

        if (doc != null)
        {
            // Bok: vonkajšia plocha vľavo = L, vpravo = P (vložený aj naložený korpus).
            if (nameRole is "bokL" or "bokP" || IsSidePanelCandidate(diel))
            {
                string? byFace = ResolveBokRoleByOuterFace(diel, doc);
                if (byFace != null)
                    return byFace;
            }

            if (nameRole is "chrbat" or "traverza" or "stojka" or "polica")
                return nameRole;

            if (IsHorizontalPanel(diel))
            {
                string? byZ = ResolveHorizontalRoleByZ(diel, doc);
                if (byZ != null)
                    return byZ;
            }
        }
        else if (nameRole is "bokL" or "bokP" or "chrbat" or "traverza" or "stojka" or "polica")
        {
            return nameRole;
        }

        return nameRole is "other" ? "priecka" : nameRole;
    }

    /// <summary>
    /// Kandidát na bok: tenký v X alebo Y, vysoký v Z — nie chrbát/traverza/polica.
    /// </summary>
    public static bool IsSidePanelCandidate(DielecModel d)
    {
        if (d.JeSuflik) return false;
        string nr = DetectRole(d.Nazov);
        if (nr is "chrbat" or "traverza" or "polica" or "stojka" or "dno" or "vrch" or "priecka")
            return false;
        if (nr is "bokL" or "bokP")
            return true;
        if (IsHorizontalPanel(d))
            return false;

        double thin = Math.Min(d.RozmerX, Math.Min(d.RozmerY, d.RozmerZ));
        if (thin < 0.5) return false;
        // Bok: hrúbka = X alebo Y (nie Z), výška Z veľká.
        bool thinX = Math.Abs(d.RozmerX - thin) <= 0.51;
        bool thinY = Math.Abs(d.RozmerY - thin) <= 0.51;
        if (!thinX && !thinY)
            return false;
        return d.RozmerZ >= thin * 3.0;
    }

    /// <summary>
    /// Bok L/P podľa vonkajšej plochy na osi medzi bokmi.
    /// Os = X alebo Y podľa väčšieho rozostupu stredov bokov (skr2: X).
    /// Os X: L = min X (vonkajšia), P = max X.
    /// Os Y: L = max Y, P = min Y (pohľad spredu, front ≈ min X).
    /// Nevyžaduje dielec medzi bokmi (naložený korpus OK).
    /// </summary>
    public static string? ResolveBokRoleByOuterFace(DielecModel diel, ExportDocument doc)
    {
        if (!IsSidePanelCandidate(diel))
            return null;

        var sides = doc.KorpusDiely.Where(IsSidePanelCandidate).ToList();
        if (sides.Count == 0)
            return null;

        static double MinX(DielecModel d) => d.WcsMinX;
        static double MaxX(DielecModel d) => d.WcsMinX + d.RozmerX;
        static double MinY(DielecModel d) => d.WcsMinY;
        static double MaxY(DielecModel d) => d.WcsMinY + d.RozmerY;
        static double CenterX(DielecModel d) => d.WcsMinX + d.RozmerX * 0.5;
        static double CenterY(DielecModel d) => d.WcsMinY + d.RozmerY * 0.5;

        // Os medzi bokmi: väčší rozostup stredov (X vs Y).
        double spanX = sides.Max(CenterX) - sides.Min(CenterX);
        double spanY = sides.Max(CenterY) - sides.Min(CenterY);
        bool axisX = spanX >= spanY - 0.1;

        DielecModel left;
        DielecModel right;
        if (axisX)
        {
            // L = vonkajšia plocha najmenšie X, P = najväčšie X (CAD: 0 / 597).
            left = sides.OrderBy(MinX).ThenBy(d => d.Cislo).First();
            right = sides.OrderByDescending(MaxX).ThenBy(d => d.Cislo).First();
        }
        else
        {
            // L = max Y (vľavo spredu), P = min Y.
            left = sides.OrderByDescending(MaxY).ThenBy(d => d.Cislo).First();
            right = sides.OrderBy(MinY).ThenBy(d => d.Cislo).First();
        }

        if (sides.Count == 1)
            return DetectRole(diel.Nazov) == "bokP" ? "bokP" : "bokL";

        bool sameExtreme = ReferenceEquals(left, right) || left.Cislo == right.Cislo;
        if (!sameExtreme)
        {
            if (ReferenceEquals(diel, left) || diel.Cislo == left.Cislo)
                return "bokL";
            if (ReferenceEquals(diel, right) || diel.Cislo == right.Cislo)
                return "bokP";
        }

        double cD = axisX ? CenterX(diel) : CenterY(diel);
        double cL = axisX ? CenterX(left) : CenterY(left);
        double cR = axisX ? CenterX(right) : CenterY(right);
        return Math.Abs(cD - cL) <= Math.Abs(cD - cR) ? "bokL" : "bokP";
    }

    /// <summary>Tenký panel v rovine XY (hrúbka = RozmerZ).</summary>
    public static bool IsHorizontalPanel(DielecModel d, double minFaceMultiple = 3.0)
    {
        if (d.JeSuflik) return false;
        double thin = Math.Min(d.RozmerX, Math.Min(d.RozmerY, d.RozmerZ));
        if (thin < 0.5) return false;
        // Vodorovná doska: najtenší rozmer je Z.
        if (Math.Abs(d.RozmerZ - thin) > 0.51)
            return false;
        return d.RozmerX >= thin * minFaceMultiple && d.RozmerY >= thin * minFaceMultiple;
    }

    /// <summary>
    /// Medzi vodorovnými panelmi: najnižšia = dno, najvyššia = vrch, ostatné = priečka.
    /// Police (názov) sa vynechajú z kandidátov dno/vrch.
    /// Ambiguous „priečka, vrch“: nikdy neberie slot dno (ten patrí samostatnému „dno“);
    /// v skupine rovnakého názvu: nižšia = priečka, vyššia = vrch.
    /// </summary>
    public static string? ResolveHorizontalRoleByZ(DielecModel diel, ExportDocument doc)
    {
        if (!IsHorizontalPanel(diel))
            return null;

        string nameRole = DetectRole(diel.Nazov);
        if (nameRole == "polica")
            return "polica";

        bool ambiguous = IsAmbiguousHorizontalName(diel.Nazov);

        var horizontals = doc.KorpusDiely
            .Where(d => IsHorizontalPanel(d))
            .Where(d => DetectRole(d.Nazov) != "polica")
            .ToList();
        if (horizontals.Count == 0)
            return null;

        static double CenterZ(DielecModel d) => d.WcsMinZ + d.RozmerZ * 0.5;

        // Ambiguous CAD názov (priečka+vrch): len medzi kópiami rovnakého mená
        // → [priecka]/[vrch], nie [dno] (dno je samostatný dielec v Exceli).
        if (ambiguous)
        {
            string baseName = StripRoleMarks(diel.Nazov);
            var peers = horizontals
                .Where(d => string.Equals(StripRoleMarks(d.Nazov), baseName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(CenterZ)
                .ThenBy(d => d.Cislo)
                .ToList();
            if (peers.Count >= 2)
            {
                if (ReferenceEquals(diel, peers[^1]))
                    return "vrch";
                return "priecka";
            }
            // Jediný ambiguous — ak je globálne najvyšší → vrch, inak priečka.
            var allOrdered = horizontals.OrderBy(CenterZ).ThenBy(d => d.Cislo).ToList();
            if (ReferenceEquals(diel, allOrdered[^1]))
                return "vrch";
            return "priecka";
        }

        var ordered = horizontals
            .OrderBy(CenterZ)
            .ThenBy(d => d.Cislo)
            .ToList();

        var lowest = ordered[0];
        var highest = ordered[^1];

        // Jediný panel — podľa názvu, inak dno.
        if (ordered.Count == 1)
        {
            if (nameRole is "dno" or "vrch" or "priecka")
                return nameRole;
            return "dno";
        }

        if (ReferenceEquals(diel, lowest))
            return "dno";
        if (ReferenceEquals(diel, highest))
            return "vrch";

        // Rovnaká výška ako dno/vrch (duplicitný solid) — podľa blízkosti.
        double z = CenterZ(diel);
        double zLow = CenterZ(lowest);
        double zHigh = CenterZ(highest);
        double tol = Math.Max(diel.RozmerZ, 20.0);
        if (Math.Abs(z - zLow) <= tol && Math.Abs(z - zLow) <= Math.Abs(z - zHigh))
            return "dno";
        if (Math.Abs(z - zHigh) <= tol)
            return "vrch";

        return "priecka";
    }

    /// <summary>Názov obsahuje priečka aj vrch/dno — rolu určí Z (nie slovo vrch/dno).</summary>
    public static bool IsAmbiguousHorizontalName(string? nazov)
    {
        string n = StripDiacritics(nazov ?? "");
        bool hasPrieck = n.Contains("prieck");
        bool hasVrch = n.Contains("vrch");
        bool hasDno = n.Contains("dno");
        return hasPrieck && (hasVrch || hasDno);
    }

    /// <summary>Odstráni prípony [dno]/[vrch]/[priecka] a #cislo pre porovnanie peerov.</summary>
    public static string StripRoleMarks(string? nazov)
    {
        string s = (nazov ?? "").Trim();
        s = Regex.Replace(s, @"\s*\[(dno|vrch|priecka)\]\s*", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*#\d+\s*$", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Pri rovnakom názve viacerých korpusových dielov doplní rolu [dno]/[vrch]/[priecka]
    /// podľa Z — ContactDetector a .xcs potom majú jednoznačné mená.
    /// </summary>
    public static void DisambiguateDuplicateHorizontalNames(ExportDocument doc)
    {
        foreach (var grp in doc.KorpusDiely
                     .GroupBy(d => (d.Nazov ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            foreach (var d in grp)
            {
                string role = DetectRole(d, doc);
                if (role is not ("dno" or "vrch" or "priecka"))
                {
                    if (d.Cislo > 0 && !d.Nazov.Contains($"#{d.Cislo}", StringComparison.Ordinal))
                        d.Nazov = $"{d.Nazov.TrimEnd()} #{d.Cislo}";
                    continue;
                }

                string mark = $"[{role}]";
                if (d.Nazov.Contains(mark, StringComparison.OrdinalIgnoreCase))
                    continue;
                d.Nazov = $"{d.Nazov.TrimEnd()} {mark}";
            }
        }
    }

    /// <summary>
    /// Kľúč CNC skupiny. Police: base+rozmery. Ostatné: pri duplicitnom názve + Cislo.
    /// </summary>
    public static string CncGroupKey(DielecModel d, ExportDocument? doc = null)
    {
        string nazov = d.Nazov ?? "";
        if (IsPolicaName(nazov))
            return $"{PolicaBaseName(nazov)}|{PolicaDimKey(d)}";

        if (doc != null && d.Cislo > 0)
        {
            int sameName = doc.KorpusDiely.Count(x =>
                string.Equals(x.Nazov, nazov, StringComparison.OrdinalIgnoreCase));
            // Po Disambiguate sú mená unikátne; pre istotu aj pred ňou.
            if (sameName > 1)
                return $"{nazov}|#{d.Cislo}";
        }

        return nazov;
    }

    /// <summary>Odstráni diakritiku, zachová veľkosť písmen (chrbát → chrbat).</summary>
    public static string RemoveDiacritics(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string form = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(form.Length);
        foreach (char c in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Malé písmená bez diakritiky (priečka → priecka).</summary>
    public static string StripDiacritics(string? s)
        => RemoveDiacritics(s).ToLowerInvariant();

    public static string PartnerRole(string nazov) => DetectRole(nazov);

    private static readonly Regex PolicaSideSuffixRegex = new(
        @"\s+[LP]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PolicaNumberInstanceSuffixRegex = new(
        @"^(.+\s\d+)\s+(\d{1,2})$", RegexOptions.CultureInvariant);

    public static bool IsPolicaName(string? nazov) => DetectRole(nazov ?? "") == "polica";

    /// <summary>Základný názov poličky bez inštancie (L/P alebo 1, 2, 3…).</summary>
    public static string PolicaBaseName(string? nazov)
    {
        if (string.IsNullOrWhiteSpace(nazov)) return "";
        nazov = nazov.TrimEnd();
        nazov = PolicaSideSuffixRegex.Replace(nazov, "").TrimEnd();
        var m = PolicaNumberInstanceSuffixRegex.Match(nazov);
        if (m.Success)
            return m.Groups[1].Value.TrimEnd();
        return nazov;
    }

    /// <summary>Rozmery poličky pre zoskupenie — CNC ak sú, inak AABB.</summary>
    public static string PolicaDimKey(DielecModel d)
    {
        if (d.HasCncRozmery)
            return $"{d.CncRozmerX:0.###};{d.CncRozmerY:0.###};{d.CncHrubka:0.###}";
        return $"{d.RozmerX:0.###};{d.RozmerY:0.###};{d.RozmerZ:0.###}";
    }

    /// <summary>Názov .xcs / workpiece — bez čísla inštancie len pri zoskupení viacerých ks rovnakej poličky.</summary>
    public static string CncFileBaseName(DielecModel d, int kusov)
    {
        string nazov = d.Nazov ?? "";
        if (!IsPolicaName(nazov)) return nazov;
        if (kusov > 1) return PolicaBaseName(nazov);
        return nazov;
    }

    /// <summary>Zobrazenie v strome — bez čísla inštancie len pri viacerých ks rovnakej poličky.</summary>
    public static string CncDisplayName(DielecModel d, int kusovInGroup = 1)
        => CncFileBaseName(d, kusovInGroup);

    /// <summary>
    /// Dotyk s policou — v UI/3D sa nezobrazuje (police držia podperky z CNC značenia).
    /// </summary>
    public static bool IsPolicaContact(ContactMark c)
        => DetectRole(c.PartA) == "polica" || DetectRole(c.PartB) == "polica";

    /// <summary>Kontakt bližšie k Max dielca pozdĺž osi styku (inak k Min).</summary>
    public static bool ContactNearMax(ContactMark c, DielecModel diel)
    {
        double partMin = c.Axis switch
        {
            0 => diel.WcsMinX,
            1 => diel.WcsMinY,
            _ => diel.WcsMinZ
        };
        double partLen = c.Axis switch
        {
            0 => diel.RozmerX,
            1 => diel.RozmerY,
            _ => diel.RozmerZ
        };
        double contact = c.Axis switch
        {
            0 => c.Center.X,
            1 => c.Center.Y,
            _ => c.Center.Z
        };
        return Math.Abs(contact - (partMin + partLen)) <= Math.Abs(contact - partMin);
    }
}

internal sealed class FallbackRules : PartRules
{
    public static readonly FallbackRules Instance = new();
}
