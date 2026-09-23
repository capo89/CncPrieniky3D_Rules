using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Dotyky šuflíkov: bok ↔ čelo / bok ↔ zad (vždy rovnaká logika).
/// Bok = vŕtanie do plochy, čelo/zad = do hrany. V UI sa nezobrazujú.
/// Kolíky: 1. od vrchu (makro 22/20), ostatné symetricky — rozteč = (L − 2·od) / (n−1).
/// </summary>
internal static class SuflikContactBuilder
{
    /// <summary>Fallback, ak sa symetrická rozteč nedá spočítať.</summary>
    public const double DefaultRoztecMm = 59.0;

    /// <summary>Bok (SufelBok3_novy): PolohaDieryY — od vrchu smerom ku dnu.</summary>
    public const double OdVrchuBokMm = 22.0;

    /// <summary>Čelo/zad (SufelCeloZad2): PolohaDiery — od vrchu smerom ku dnu.</summary>
    public const double OdVrchuCeloZadMm = 20.0;

    public static void Attach(ExportDocument doc)
    {
        // Odstráň predchádzajúce auto-dotyky (pri opätovnom načítaní / refresh počte)
        doc.Dotyky.RemoveAll(c => c.JeSuflikAuto);

        var individuals = doc.SuflikDiely.Where(d => !d.JeSuflikPozicia).ToList();
        if (individuals.Count < 2) return;

        var found = ContactDetector.Find(individuals);
        int nextCislo = doc.Dotyky.Count == 0 ? 1 : doc.Dotyky.Max(c => c.Cislo) + 1;

        foreach (var c in found)
        {
            var a = Find(doc, c.PartA);
            var b = Find(doc, c.PartB);
            if (a == null || b == null) continue;
            if (!IsBokCeloAleboZad(a, b)) continue;

            c.JeSuflikAuto = true;
            c.Cislo = nextCislo++;
            c.Oznacenie = "šufel";
            ApplyKoliky(doc, c, a, b);
            doc.Dotyky.Add(c);
        }
    }

    /// <summary>Po zmene počtu kolíkov na pozícii šufľa — sync sérií na auto-dotykoch.</summary>
    public static void RefreshKolikyForPozicia(ExportDocument doc, DielecModel pozicia)
    {
        if (!pozicia.JeSuflikPozicia) return;
        double z0 = pozicia.WcsMinZ;
        double z1 = pozicia.WcsMinZ + pozicia.RozmerZ;

        foreach (var c in doc.Dotyky.Where(x => x.JeSuflikAuto))
        {
            var a = Find(doc, c.PartA);
            var b = Find(doc, c.PartB);
            if (a == null || b == null) continue;
            if (!OverlapsZ(a, z0, z1) && !OverlapsZ(b, z0, z1)) continue;
            ApplyKoliky(doc, c, a, b);
        }
    }

    /// <summary>Od vrchu podľa typu dielca (makro).</summary>
    public static double OdVrchuForPart(DielecModel diel)
        => IsTyp(diel, "bok") ? OdVrchuBokMm : OdVrchuCeloZadMm;

    /// <summary>
    /// Symetrická rozteč pre šufeľ: (dĺžka dotyku − 2·odVrchu) / (počet − 1).
    /// Od vrchu = pozícia prvého kolíka (z makra); zvyšok sa rovnomerne rozloží.
    /// </summary>
    public static bool TryComputeRoztec(ContactMark c, double odVrchu, int pocet, out double roztec)
        => SkrutkaLayout.TryComputeSymetricRoztec(
            SkrutkaLayout.PrimaryLength(c), odVrchu, pocet, out roztec, out _);

    /// <summary>Najväčšia dĺžka auto-dotyku viazaného na diel (pre XCS).</summary>
    public static ContactMark? FindPrimaryAutoContact(ExportDocument doc, DielecModel diel)
    {
        return doc.Dotyky
            .Where(c => c.JeSuflikAuto)
            .Where(c =>
                string.Equals(c.PartA, diel.Nazov, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.PartB, diel.Nazov, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(SkrutkaLayout.PrimaryLength)
            .FirstOrDefault();
    }

    private static void ApplyKoliky(ExportDocument doc, ContactMark c, DielecModel a, DielecModel b)
    {
        var poz = FindParentPozicia(doc, a) ?? FindParentPozicia(doc, b);
        int pocet = poz?.PocetKolikov > 0 ? poz.PocetKolikov : 3;

        // 3D náhľad: spoločná séria od vrchu dotyku; od = bok (22), aby sedelo s plochou boku.
        // Čelo/zad v XCS majú Poloha 20 — lokálny posun makra, rozteč sa počíta zvlášť.
        double od = OdVrchuBokMm;
        if (!TryComputeRoztec(c, od, pocet, out double roztec))
            roztec = DefaultRoztecMm;

        c.ReplaceAllSerie(new[]
        {
            new KolikSerie
            {
                PocetKolikov = pocet,
                RoztecKolikov = roztec,
                OdPredu = od,
                ZoStredu = false,
                // Od vrchného okraja (väčšie Z / primMax) smerom ku dnu.
                ZDruhejStrany = true,
            }
        });
    }

    private static bool IsBokCeloAleboZad(DielecModel a, DielecModel b)
    {
        bool aBok = IsTyp(a, "bok");
        bool bBok = IsTyp(b, "bok");
        bool aCeloZad = IsTyp(a, "celo") || IsTyp(a, "zad");
        bool bCeloZad = IsTyp(b, "celo") || IsTyp(b, "zad");
        return (aBok && bCeloZad) || (bBok && aCeloZad);
    }

    private static bool IsTyp(DielecModel d, string typ)
    {
        if (!string.IsNullOrEmpty(d.SufelTypDielu)
            && d.SufelTypDielu.Equals(typ, StringComparison.OrdinalIgnoreCase))
            return true;
        string n = (d.Nazov ?? "").ToLowerInvariant();
        if (typ == "celo") return n.Contains("čelo") || n.Contains("celo");
        if (typ == "zad") return n.Contains("zad") && !n.Contains("čelo") && !n.Contains("celo");
        return n.Contains(typ);
    }

    internal static DielecModel? FindParentPozicia(ExportDocument doc, DielecModel part)
    {
        double cz = part.WcsMinZ + part.RozmerZ * 0.5;
        return doc.SuflikDiely
            .Where(d => d.JeSuflikPozicia)
            .OrderBy(p => Math.Abs(p.WcsMinZ + p.RozmerZ * 0.5 - cz))
            .FirstOrDefault();
    }

    private static bool OverlapsZ(DielecModel d, double z0, double z1)
    {
        double a0 = d.WcsMinZ;
        double a1 = d.WcsMinZ + d.RozmerZ;
        return a0 < z1 + 1 && a1 > z0 - 1;
    }

    private static DielecModel? Find(ExportDocument doc, string nazov)
        => doc.Diely.FirstOrDefault(d =>
            string.Equals(d.Nazov, nazov, StringComparison.OrdinalIgnoreCase));
}
