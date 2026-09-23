namespace CncPrieniky3D.Models;

/// <summary>Výpočet rozteče a dĺžky styčnej plochy pre skrutky.</summary>
internal static class SkrutkaLayout
{
    /// <summary>Dĺžka styčnej plochy pozdĺž hlavného (dlhšieho) smeru.</summary>
    public static double PrimaryLength(ContactMark c)
    {
        GetPrimaryAxes(c, out int primary, out _);
        double[] size = { c.Size.X, c.Size.Y, c.Size.Z };
        return size[primary];
    }

    public static void GetPrimaryAxes(ContactMark c, out int primary, out int secondary)
    {
        int n = c.Axis;
        int u = n == 0 ? 1 : 0;
        int v = n == 2 ? 1 : 2;
        if (u == v) v = n == 1 ? 2 : 1;

        double[] size = { c.Size.X, c.Size.Y, c.Size.Z };
        primary = size[u] >= size[v] ? u : v;
        secondary = primary == u ? v : u;
    }

    /// <summary>
    /// Rozpätie kolíkov na dotyku: vzdialenosť 1. ↔ posledný kolík pozdĺž hlavnej osi
    /// a odsadenie 1. kolíka od min hrany dotyku.
    /// </summary>
    public static bool TryKolikSpanAlongContact(
        ContactMark c, out double span, out double firstFromMin, out string? error)
    {
        span = 0;
        firstFromMin = 0;
        error = null;

        if (c.KolikSerie.Count == 0 || c.CelkovyPocetKolikov < 1)
        {
            error = "Na ploche nie sú kolíky — variant „medzi kolíkmi“ vyžaduje aspoň 1 kolík.";
            return false;
        }

        GetPrimaryAxes(c, out int primary, out _);
        double[] size = { c.Size.X, c.Size.Y, c.Size.Z };
        double[] center = { c.Center.X, c.Center.Y, c.Center.Z };
        double primLen = size[primary];
        double primMin = center[primary] - primLen * 0.5;
        double primMax = center[primary] + primLen * 0.5;

        double minAlong = double.PositiveInfinity;
        double maxAlong = double.NegativeInfinity;
        int count = 0;

        foreach (var serie in c.KolikSerie)
        {
            if (serie.PocetKolikov < 1)
                continue;

            for (int i = 0; i < serie.PocetKolikov; i++)
            {
                double along;
                if (serie.ZoStredu)
                {
                    along = (primMin + primMax) * 0.5
                        - (serie.PocetKolikov - 1) * 0.5 * serie.RoztecKolikov
                        + i * serie.RoztecKolikov;
                }
                else if (serie.ZDruhejStrany)
                {
                    along = primMax - serie.OdPredu - i * serie.RoztecKolikov;
                }
                else
                {
                    along = primMin + serie.OdPredu + i * serie.RoztecKolikov;
                }

                if (along < minAlong) minAlong = along;
                if (along > maxAlong) maxAlong = along;
                count++;
            }
        }

        if (count < 1)
        {
            error = "Na ploche nie sú platné kolíky.";
            return false;
        }

        if (count == 1)
        {
            // Jeden kolík = nulové rozpätie; symetria len so stredom (1 skrutka / odPredu=0).
            span = 0;
            firstFromMin = minAlong - primMin;
            return true;
        }

        span = maxAlong - minAlong;
        firstFromMin = minAlong - primMin;
        if (span < 1e-6)
        {
            error = "Kolíky majú rovnakú pozíciu — nie je medzi nimi dĺžka.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Rozteč = (dĺžka − 2·odPredu) / (počet − 1). Pre 1 skrutku rozteč nie je potrebná (0).
    /// </summary>
    public static bool TryComputeSymetricRoztec(
        double dlzkaPlochy,
        double odPredu,
        int pocet,
        out double roztec,
        out string? error)
    {
        roztec = 0;
        error = null;

        if (odPredu < 0)
        {
            error = "Od predu musí byť ≥ 0.";
            return false;
        }

        if (pocet < 1)
        {
            error = "Počet skrutiek musí byť ≥ 1.";
            return false;
        }

        if (pocet == 1)
        {
            if (2 * odPredu > dlzkaPlochy + 1e-6)
            {
                error = $"Od predu ({odPredu:0.##}) je príliš veľké pre dĺžku {dlzkaPlochy:0.##} mm.";
                return false;
            }
            roztec = 0;
            return true;
        }

        double usable = dlzkaPlochy - 2 * odPredu;
        if (usable <= 1e-6)
        {
            error =
                $"Symetrické: (dĺžka {dlzkaPlochy:0.##} − 2×{odPredu:0.##}) musí byť > 0.";
            return false;
        }

        roztec = usable / (pocet - 1);
        return true;
    }

    /// <summary>
    /// Dĺžka a od predu (od min hrany dotyku) pre symetrický výpočet.
    /// Variant medzi kolíkmi: dĺžka = 1.↔posledný kolík, Od predu UI = od 1. kolíka.
    /// </summary>
    public static bool TryResolveSymetricParams(
        ContactMark contact, SkrutkaSerie serie,
        out double dlzka, out double odPreduOdMin, out string? error)
    {
        dlzka = 0;
        odPreduOdMin = serie.OdPredu;
        error = null;

        if (!serie.Symetricke)
            return true;

        if (serie.SymetriaMedziKolikmi)
        {
            if (!TryKolikSpanAlongContact(contact, out dlzka, out double firstFromMin, out error))
                return false;
            odPreduOdMin = firstFromMin + serie.OdPredu;
            return true;
        }

        dlzka = PrimaryLength(contact);
        odPreduOdMin = serie.OdPredu;
        return true;
    }

    /// <summary>Doplní rozteč podľa plochy / kolíkov; pri neúspechu vráti chybu.</summary>
    public static bool ApplySymetricIfNeeded(ContactMark contact, SkrutkaSerie serie, out string? error)
    {
        error = null;
        if (!serie.Symetricke)
            return true;

        serie.ZDruhejStrany = false;
        if (!TryResolveSymetricParams(contact, serie, out double len, out double odMin, out error))
            return false;
        if (!TryComputeSymetricRoztec(len, serie.SymetriaMedziKolikmi ? serie.OdPredu : odMin,
                serie.PocetSkrutiek, out double roztec, out error))
            return false;

        // Pre medzi kolíkmi: formula používa Od predu od 1. kolíka (= serie.OdPredu);
        // odMin je len na umiestnenie (viewer / XCS).
        _ = odMin;
        serie.RoztecSkrutiek = roztec;
        return true;
    }
}
