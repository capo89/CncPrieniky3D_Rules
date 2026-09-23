using System.Globalization;
using System.Text;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Strom dielov: korpus, šufle (deti), sekcia „čista miera“.
/// </summary>
internal static class PartTreeBuilder
{
    public const string CistaMieraSectionTitle = "čista miera";

    public static List<PartTreeNode> Build(ExportDocument doc)
    {
        var roots = new List<PartTreeNode>();
        var cista = doc.Diely.Where(JeCistaMiera).OrderBy(d => d.Cislo).ToList();
        var cistaSet = cista.ToHashSet();

        foreach (var group in doc.KorpusDiely
                     .Where(d => !cistaSet.Contains(d))
                     .GroupBy(d => PartRules.CncGroupKey(d, doc), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Min(d => d.Cislo)))
        {
            var list = group.OrderBy(d => d.Cislo).ToList();
            var rep = list[0];
            rep.PocetKusov = list.Count;
            // Ostatné inštancie nechávame v Diely (3D), v strome len reprezentant.
            roots.Add(new PartTreeNode(rep));
        }

        var positions = doc.SuflikDiely
            .Where(d => d.JeSuflikPozicia)
            .OrderBy(d => d.Cislo)
            .ToList();
        var individuals = doc.SuflikDiely
            .Where(d => !d.JeSuflikPozicia && !cistaSet.Contains(d))
            .ToList();
        var assigned = new HashSet<DielecModel>();

        foreach (var poz in positions)
        {
            var parent = new PartTreeNode(poz) { IsExpanded = false };
            foreach (var child in individuals
                         .Where(d => ReferenceEquals(SuflikContactBuilder.FindParentPozicia(doc, d), poz))
                         .OrderBy(TypOrder)
                         .ThenBy(d => d.Cislo))
            {
                parent.Children.Add(new PartTreeNode(child));
                assigned.Add(child);
            }
            roots.Add(parent);
        }

        foreach (var orphan in individuals.Where(d => !assigned.Contains(d)).OrderBy(d => d.Cislo))
            roots.Add(new PartTreeNode(orphan));

        if (cista.Count > 0)
        {
            var section = new PartTreeNode(CistaMieraSectionTitle);
            foreach (var d in cista)
                section.Children.Add(new PartTreeNode(d));
            roots.Add(section);
        }

        return roots;
    }

    public static bool JeCistaMiera(DielecModel d)
    {
        if (d.JeSuflikPozicia) return false;
        string n = Normalize(d.Nazov);
        return n.Contains("cista miera") || n.Contains("cistamiera");
    }

    public static PartTreeNode? FindNode(IEnumerable<PartTreeNode> roots, DielecModel dielec)
    {
        foreach (var n in roots)
        {
            if (n.Dielec != null && ReferenceEquals(n.Dielec, dielec))
                return n;
            var nested = FindNode(n.Children, dielec);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string form = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(form.Length);
        foreach (char c in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static int TypOrder(DielecModel d)
    {
        string t = (d.SufelTypDielu ?? "").ToLowerInvariant();
        return t switch
        {
            "bok" => 0,
            "celo" => 1,
            "zad" => 2,
            "dno" => 3,
            _ => 9
        };
    }
}
