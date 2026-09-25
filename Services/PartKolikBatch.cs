using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Kolíkovanie podľa vybraných dielcov: nájde dotyky s zvolenými partnermi
/// a skontroluje zhodu rozmerov podľa osi (Y = hĺbka spoločná, X = šírka osobitne).
/// </summary>
internal static class PartKolikBatch
{
    public const double DimTolMm = 2.0;

    /// <summary>
    /// Os merania dĺžky dielca relevantná pre sériu kolíkov na danom kontakte.
    /// Axis 0 (normála X, bok) → hĺbka Y; Axis 1 (normála Y, chrbát) → šírka X.
    /// </summary>
    public static int SeriesDimAxis(ContactMark c) => c.Axis switch
    {
        0 => 1, // Y
        1 => 0, // X
        _ => 1
    };

    public static double DimOf(DielecModel d, int axis) => axis switch
    {
        0 => d.RozmerX,
        1 => d.RozmerY,
        _ => d.RozmerZ
    };

    public static string DimAxisName(int axis) => axis switch
    {
        0 => "X (šírka)",
        1 => "Y (hĺbka)",
        _ => "Z"
    };

    public static bool ContactInvolves(ContactMark c, DielecModel d)
    {
        if (!string.IsNullOrEmpty(d.SkrinkaKey) &&
            !string.IsNullOrEmpty(c.SkrinkaKey) &&
            !string.Equals(c.SkrinkaKey, d.SkrinkaKey, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(c.PartA, d.Nazov, StringComparison.OrdinalIgnoreCase)
               || string.Equals(c.PartB, d.Nazov, StringComparison.OrdinalIgnoreCase);
    }

    public static string? PartnerName(ContactMark c, DielecModel selected)
    {
        if (string.Equals(c.PartA, selected.Nazov, StringComparison.OrdinalIgnoreCase))
            return c.PartB;
        if (string.Equals(c.PartB, selected.Nazov, StringComparison.OrdinalIgnoreCase))
            return c.PartA;
        return null;
    }

    public static bool PartnerRoleMatches(ExportDocument doc, ContactMark c, DielecModel selected, ISet<string> partnerRoles)
    {
        string? partnerName = PartnerName(c, selected);
        if (partnerName == null) return false;
        var partner = doc.Diely.FirstOrDefault(x =>
            string.Equals(x.Nazov, partnerName, StringComparison.OrdinalIgnoreCase));
        if (partner == null) return false;
        string role = PartRules.DetectRole(partner, doc);
        return partnerRoles.Contains(role);
    }

    public static List<ContactMark> FindTargets(
        IReadOnlyList<(ExportDocument Doc, DielecModel Dielec)> selected,
        ISet<string> partnerRoles)
    {
        var result = new List<ContactMark>();
        foreach (var (doc, diel) in selected)
        {
            foreach (var c in doc.Dotyky.Where(x => !x.JeSuflikAuto))
            {
                if (!ContactInvolves(c, diel)) continue;
                if (!PartnerRoleMatches(doc, c, diel, partnerRoles)) continue;
                if (!result.Contains(c))
                    result.Add(c);
            }
        }
        return result;
    }

    /// <summary>
    /// True = rozmery na osi série sú v tolerancii (smie jedna séria).
    /// Pri osi X (šírka) mimo tolerancie → false (chrbát treba osobitne).
    /// </summary>
    public static bool TryValidateSeriesDims(
        IReadOnlyList<(ExportDocument Doc, DielecModel Dielec)> selected,
        IReadOnlyList<ContactMark> targets,
        out string message)
    {
        if (targets.Count == 0)
        {
            message = "Nenašli sa žiadne dotyky pre zvolených partnerov.";
            return false;
        }

        // Dominantná os série podľa cieľových kontaktov
        var axes = targets.Select(SeriesDimAxis).Distinct().ToList();
        if (axes.Count > 1)
        {
            message = "Cieľové dotyky majú rôzne osi (Y vs X). Rozdeľ kolíkovanie (napr. najprv boky, potom chrbát).";
            return false;
        }

        int dimAxis = axes[0];
        var dims = selected.Select(s => DimOf(s.Dielec, dimAxis)).ToList();
        double min = dims.Min();
        double max = dims.Max();
        double spread = max - min;

        if (spread <= DimTolMm)
        {
            message =
                $"OK: {DimAxisName(dimAxis)} ≈ {min:0.#}–{max:0.#} mm (±{DimTolMm:0.#}). " +
                $"Séria na {targets.Count} plôch.";
            return true;
        }

        // Y (hĺbka): ešte povolíme s varovaním? User povedal Y môže byť rovnaké/+- = same series.
        // X: osobitne pri veľkých rozdieloch.
        if (dimAxis == 1)
        {
            // Hĺbka mimo tol — stále varovanie, ale dovolíme (user: +- = rovnaká)
            message =
                $"Varovanie: {DimAxisName(dimAxis)} sa líši o {spread:0.#} mm " +
                $"({min:0.#}–{max:0.#}). Pokračovať jednou sériou?";
            return true; // caller may still ask
        }

        message =
            $"{DimAxisName(dimAxis)} sa líši o {spread:0.#} mm ({min:0.#}–{max:0.#}). " +
            "Pri šírke (napr. chrbát) priraď kolíky osobitne podľa skupín rovnakých rozmerov.";
        return false;
    }

    public static HashSet<string> AvailablePartnerRoles(
        IReadOnlyList<(ExportDocument Doc, DielecModel Dielec)> selected)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (doc, diel) in selected)
        {
            foreach (var c in doc.Dotyky.Where(x => !x.JeSuflikAuto && ContactInvolves(x, diel)))
            {
                string? pn = PartnerName(c, diel);
                if (pn == null) continue;
                var p = doc.Diely.FirstOrDefault(x =>
                    string.Equals(x.Nazov, pn, StringComparison.OrdinalIgnoreCase));
                if (p != null)
                    roles.Add(PartRules.DetectRole(p, doc));
            }
        }
        return roles;
    }
}
