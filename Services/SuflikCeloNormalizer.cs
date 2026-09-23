using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Zrkadlí inštancie šuflíkov, kde je „čelo“ bližšie k chrbátu než k prednej strane korpusu,
/// aby všetky čelá boli na rovnakej (prednej) strane.
/// </summary>
internal static class SuflikCeloNormalizer
{
    public static int Normalize(ExportDocument doc)
    {
        var individuals = doc.SuflikDiely.Where(d => !d.JeSuflikPozicia).ToList();
        if (individuals.Count == 0) return 0;

        if (!TryGetFrontBack(doc, out int axis, out double back, out double front))
            return 0;

        var groups = GroupByPoziciaOrZ(doc, individuals);
        int flipped = 0;

        foreach (var group in groups)
        {
            if (group.Count == 0) continue;

            var celo = group.FirstOrDefault(d =>
                d.SufelTypDielu.Equals("celo", StringComparison.OrdinalIgnoreCase)
                || PartRules.StripDiacritics(d.Nazov).Contains("celo"));

            if (celo == null) continue;

            double celoC = Center(celo, axis);
            double distBack = Math.Abs(celoC - back);
            double distFront = Math.Abs(celoC - front);
            if (distBack + 1.0 < distFront)
            {
                MirrorGroup(group, axis);
                flipped++;
            }
        }

        return flipped;
    }

    /// <summary>Predná / zadná strana korpusu (WCS) — pre ABS aj šufle.</summary>
    internal static bool TryGetFrontBack(
        ExportDocument doc, out int axis, out double back, out double front)
    {
        axis = 0;
        back = 0;
        front = 0;

        var korpus = doc.KorpusDiely.ToList();
        if (korpus.Count == 0) return false;

        // Diakritika: „chrbát“ ≠ „chrbat“ pri OrdinalIgnoreCase — StripDiacritics.
        var chrbat = korpus.FirstOrDefault(d =>
            PartRules.StripDiacritics(d.Nazov).Contains("chrbat"));
        var predna = korpus.FirstOrDefault(d =>
            PartRules.StripDiacritics(d.Nazov).Contains("predn"));
        var zadna = korpus.FirstOrDefault(d =>
        {
            string n = PartRules.StripDiacritics(d.Nazov);
            return n.Contains("zadn") && !n.Contains("chrbat");
        });

        DielecModel? backPart = chrbat ?? zadna;
        DielecModel? frontPart = predna;

        if (backPart != null && frontPart != null)
        {
            double dx = Math.Abs(Center(frontPart, 0) - Center(backPart, 0));
            double dy = Math.Abs(Center(frontPart, 1) - Center(backPart, 1));
            axis = dx >= dy ? 0 : 1;
            back = Center(backPart, axis);
            front = Center(frontPart, axis);
            return Math.Abs(front - back) > 1.0;
        }

        // Len chrbat / zadná / predná traverza — predok = opačný kraj korpusu.
        DielecModel? depthRef = backPart ?? frontPart;
        if (depthRef != null)
        {
            // Hĺbka = tenší z vodorovných rozmerov referenčného dielu (chrbat je tenký).
            int depthAxis = depthRef.RozmerX <= depthRef.RozmerY ? 0 : 1;
            axis = depthAxis;
            double extentMin = korpus.Min(d => GetMin(d, depthAxis));
            double extentMax = korpus.Max(d => GetMin(d, depthAxis) + GetSize(d, depthAxis));
            double refC = Center(depthRef, depthAxis);
            if (backPart != null)
            {
                back = refC;
                front = Math.Abs(back - extentMin) < Math.Abs(back - extentMax) ? extentMax : extentMin;
            }
            else
            {
                front = refC;
                back = Math.Abs(front - extentMin) < Math.Abs(front - extentMax) ? extentMax : extentMin;
            }
            return Math.Abs(front - back) > 1.0;
        }

        // Fallback: čelo vs zad zo šuflíkov + chrbat / max rozsah korpusu
        var cela = doc.SuflikDiely.Where(d => !d.JeSuflikPozicia && (
            d.SufelTypDielu.Equals("celo", StringComparison.OrdinalIgnoreCase)
            || PartRules.StripDiacritics(d.Nazov).Contains("celo"))).ToList();
        var zady = doc.SuflikDiely.Where(d => !d.JeSuflikPozicia && (
            d.SufelTypDielu.Equals("zad", StringComparison.OrdinalIgnoreCase)
            || (PartRules.StripDiacritics(d.Nazov).Contains("zad")
                && !PartRules.StripDiacritics(d.Nazov).Contains("celo")))).ToList();

        if (cela.Count == 0 || zady.Count == 0) return false;

        double sepX = Math.Abs(cela.Average(d => Center(d, 0)) - zady.Average(d => Center(d, 0)));
        double sepY = Math.Abs(cela.Average(d => Center(d, 1)) - zady.Average(d => Center(d, 1)));
        axis = sepX >= sepY ? 0 : 1;
        int ax = axis;

        double kMin = korpus.Min(d => GetMin(d, ax));
        double kMax = korpus.Max(d => GetMin(d, ax) + GetSize(d, ax));

        if (chrbat != null)
        {
            back = Center(chrbat, ax);
            front = Math.Abs(back - kMin) < Math.Abs(back - kMax) ? kMax : kMin;
        }
        else
        {
            // Predpoklad: väčšina čiel je vpredu
            double avgCelo = cela.Average(d => Center(d, ax));
            if (Math.Abs(avgCelo - kMax) <= Math.Abs(avgCelo - kMin))
            {
                front = kMax;
                back = kMin;
            }
            else
            {
                front = kMin;
                back = kMax;
            }
        }

        return Math.Abs(front - back) > 1.0;
    }

    private static List<List<DielecModel>> GroupByPoziciaOrZ(
        ExportDocument doc, List<DielecModel> individuals)
    {
        var positions = doc.SuflikDiely.Where(d => d.JeSuflikPozicia).ToList();
        if (positions.Count > 0)
        {
            var map = positions.ToDictionary(p => p, _ => new List<DielecModel>());
            foreach (var part in individuals)
            {
                var best = positions
                    .OrderBy(poz => Math.Abs(Center(part, 2) - Center(poz, 2)))
                    .ThenBy(poz => Dist2(part, poz))
                    .First();
                map[best].Add(part);
            }
            return map.Values.Where(g => g.Count > 0).ToList();
        }

        // Cluster podľa Z stredu (medzera ≥ 50 mm)
        var ordered = individuals.OrderBy(d => Center(d, 2)).ToList();
        var groups = new List<List<DielecModel>>();
        List<DielecModel>? cur = null;
        double lastZ = double.NaN;
        foreach (var p in ordered)
        {
            double z = Center(p, 2);
            if (cur == null || double.IsNaN(lastZ) || Math.Abs(z - lastZ) > 50)
            {
                cur = new List<DielecModel>();
                groups.Add(cur);
            }
            cur.Add(p);
            lastZ = z;
        }
        return groups;
    }

    private static void MirrorGroup(List<DielecModel> parts, int axis)
    {
        double gMin = parts.Min(p => GetMin(p, axis));
        double gMax = parts.Max(p => GetMin(p, axis) + GetSize(p, axis));
        double c = 0.5 * (gMin + gMax);

        foreach (var p in parts)
        {
            double oldWcs = GetMin(p, axis);
            double size = GetSize(p, axis);
            double newWcs = 2.0 * c - oldWcs - size;
            double delta = newWcs - oldWcs;
            SetMin(p, axis, newWcs);
            SetBlockMin(p, axis, GetBlockMin(p, axis) + delta);
            FlipLocal(p, axis);
        }
    }

    private static void FlipLocal(DielecModel p, int axis)
    {
        double size = GetSize(p, axis);
        foreach (var b in p.Body)
            FlipBod(b, axis, size);
        foreach (var pl in p.Plochy)
        {
            if (axis == 0) pl.PosX = size - pl.PosX;
            else if (axis == 1) pl.PosY = size - pl.PosY;
            else pl.PosZ = size - pl.PosZ;
        }
        foreach (var d in p.Diery)
        {
            if (axis == 0) d.PosX = size - d.PosX;
            else if (axis == 1) d.PosY = size - d.PosY;
            else d.PosZ = size - d.PosZ;
        }
        foreach (var z in p.CncZnacenia)
        {
            if (axis == 0) z.PosX = size - z.PosX;
            else if (axis == 1) z.PosY = size - z.PosY;
            else z.PosZ = size - z.PosZ;
        }
        foreach (var v in p.Vyrezy)
        {
            if (axis == 0) v.PosX = size - v.PosX;
            else if (axis == 1) v.PosY = size - v.PosY;
            else v.PosZ = size - v.PosZ;
            foreach (var b in v.Body)
                FlipBod(b, axis, size);
        }
    }

    private static void FlipBod(BodModel b, int axis, double size)
    {
        if (axis == 0) b.PosX = size - b.PosX;
        else if (axis == 1) b.PosY = size - b.PosY;
        else b.PosZ = size - b.PosZ;
    }

    private static double Center(DielecModel d, int axis)
        => GetMin(d, axis) + GetSize(d, axis) * 0.5;

    private static double GetMin(DielecModel d, int axis) => axis switch
    {
        1 => d.WcsMinY,
        2 => d.WcsMinZ,
        _ => d.WcsMinX
    };

    private static double GetSize(DielecModel d, int axis) => axis switch
    {
        1 => d.RozmerY,
        2 => d.RozmerZ,
        _ => d.RozmerX
    };

    private static double GetBlockMin(DielecModel d, int axis) => axis switch
    {
        1 => d.MinY,
        2 => d.MinZ,
        _ => d.MinX
    };

    private static void SetMin(DielecModel d, int axis, double v)
    {
        if (axis == 1) d.WcsMinY = v;
        else if (axis == 2) d.WcsMinZ = v;
        else d.WcsMinX = v;
    }

    private static void SetBlockMin(DielecModel d, int axis, double v)
    {
        if (axis == 1) d.MinY = v;
        else if (axis == 2) d.MinZ = v;
        else d.MinX = v;
    }

    private static double Dist2(DielecModel a, DielecModel b)
    {
        double dx = Center(a, 0) - Center(b, 0);
        double dy = Center(a, 1) - Center(b, 1);
        double dz = Center(a, 2) - Center(b, 2);
        return dx * dx + dy * dy + dz * dz;
    }
}
