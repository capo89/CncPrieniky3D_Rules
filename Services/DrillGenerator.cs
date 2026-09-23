using System.Windows.Media.Media3D;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>Jedno vygenerované vŕtanie na dielec.</summary>
public sealed class GeneratedDrill
{
    public int DielCislo { get; init; }
    public string DielNazov { get; init; } = "";
    public string Typ { get; init; } = ""; // Kolik / Skrutka
    public string Strana { get; init; } = ""; // Plocha / Hrana
    public string Dotyk { get; init; } = "";
    public double LocalX { get; init; }
    public double LocalY { get; init; }
    public double LocalZ { get; init; }
    public double FaceX { get; init; }
    public double FaceY { get; init; }
    public double FaceZ { get; init; }
    public double Priemer { get; init; }
    public double Hlbka { get; init; }
}

internal static class DrillGenerator
{
    private const double KolikPriemerMm = 8.0;
    private const double KolikDoHranyMm = 23.0;
    private const double KolikDoPlochyMm = 13.0;
    private const double SkrutkaPriemerMm = 3.0;
    public const string GeneratedHandlePrefix = "GEN:";

    public static List<GeneratedDrill> Generate(ExportDocument doc)
    {
        var list = new List<GeneratedDrill>();

        foreach (var c in doc.Dotyky)
        {
            if (c.JeSuflikAuto)
                continue; // šufle → makro, nie CreateDrill do CNC značenia
            if (!c.MaKoliky && !c.MaSkrutky)
                continue;

            DielecModel? partA = doc.Diely.FirstOrDefault(d =>
                string.Equals(d.Nazov, c.PartA, StringComparison.OrdinalIgnoreCase));
            DielecModel? partB = doc.Diely.FirstOrDefault(d =>
                string.Equals(d.Nazov, c.PartB, StringComparison.OrdinalIgnoreCase));

            ResolveSides(c, partA, partB,
                out bool aPlocha, out bool bPlocha,
                out double depthA, out double depthB,
                out DielecModel? plocha, out double plochaThickness);

            if (c.MaKoliky)
            {
                foreach (var pt in SceneBuilder.GetKolikContactPoints(doc, c, partA, partB))
                {
                    if (partA != null)
                    {
                        list.Add(MakeDrill(partA, pt, aPlocha ? "Plocha" : "Hrana", "Kolik",
                            c.LabelText, KolikPriemerMm, depthA));
                    }
                    if (partB != null)
                    {
                        list.Add(MakeDrill(partB, pt, bPlocha ? "Plocha" : "Hrana", "Kolik",
                            c.LabelText, KolikPriemerMm, depthB));
                    }
                }
            }

            if (c.MaSkrutky && plocha != null)
            {
                foreach (var pt in SceneBuilder.GetSkrutkaContactPoints(doc, c))
                {
                    list.Add(MakeDrill(plocha, pt, "Plocha", "Skrutka",
                        c.LabelText, SkrutkaPriemerMm, plochaThickness));
                }
            }
        }

        return list;
    }

    /// <summary>Odstráni predchádzajúce GEN: značenia a doplní nové z vygenerovaných vŕtaní.</summary>
    public static void ApplyToCncZnacenia(ExportDocument doc, IReadOnlyList<GeneratedDrill> drills)
    {
        foreach (var d in doc.Diely)
            d.CncZnacenia.RemoveAll(z =>
                z.Handle.StartsWith(GeneratedHandlePrefix, StringComparison.OrdinalIgnoreCase));

        var byPart = drills.GroupBy(x => x.DielNazov, StringComparer.OrdinalIgnoreCase);
        foreach (var g in byPart)
        {
            var diel = doc.Diely.FirstOrDefault(d =>
                string.Equals(d.Nazov, g.Key, StringComparison.OrdinalIgnoreCase));
            if (diel == null) continue;

            int n = diel.CncZnacenia.Count > 0 ? diel.CncZnacenia.Max(z => z.Cislo) + 1 : 1;
            foreach (var drill in g)
            {
                diel.CncZnacenia.Add(new CncZnacenieModel
                {
                    Cislo = n++,
                    Typ = drill.Typ,
                    PosX = drill.FaceX,
                    PosY = drill.FaceY,
                    PosZ = drill.FaceZ,
                    Priemer = drill.Priemer,
                    Vrstva = drill.Typ,
                    Handle = $"{GeneratedHandlePrefix}{drill.Typ}/{drill.Strana}/{drill.Dotyk}"
                });
            }
        }
    }

    private static GeneratedDrill MakeDrill(
        DielecModel diel, Point3D wcs, string strana, string typ, string dotyk,
        double priemer, double hlbka)
    {
        double lx = wcs.X - diel.WcsMinX;
        double ly = wcs.Y - diel.WcsMinY;
        double lz = wcs.Z - diel.WcsMinZ;
        LocalToFace(diel, lx, ly, lz, out double fx, out double fy, out double fz);

        return new GeneratedDrill
        {
            DielCislo = diel.Cislo,
            DielNazov = diel.Nazov,
            Typ = typ,
            Strana = strana,
            Dotyk = dotyk,
            LocalX = lx,
            LocalY = ly,
            LocalZ = lz,
            FaceX = fx,
            FaceY = fy,
            FaceZ = fz,
            Priemer = priemer,
            Hlbka = hlbka
        };
    }

    private static void ResolveSides(
        ContactMark c,
        DielecModel? partA,
        DielecModel? partB,
        out bool aPlocha,
        out bool bPlocha,
        out double depthA,
        out double depthB,
        out DielecModel? plocha,
        out double plochaThickness)
    {
        Vector3D towardB = c.Axis switch
        {
            0 => new Vector3D(1, 0, 0),
            1 => new Vector3D(0, 1, 0),
            _ => new Vector3D(0, 0, 1)
        };

        double aMid = MidAlong(partA, c.Axis);
        double bMid = MidAlong(partB, c.Axis);
        if (bMid < aMid)
            towardB = -towardB;

        aPlocha = IsPlocha(partA, c.Axis);
        bPlocha = IsPlocha(partB, c.Axis);

        depthA = aPlocha ? KolikDoPlochyMm : KolikDoHranyMm;
        depthB = bPlocha ? KolikDoPlochyMm : KolikDoHranyMm;
        if (aPlocha == bPlocha)
        {
            depthA = KolikDoHranyMm;
            depthB = KolikDoPlochyMm;
            aPlocha = false;
            bPlocha = true;
        }

        bool plochaJeB = bPlocha;
        plocha = plochaJeB ? partB : partA;
        plochaThickness = ThicknessAlong(plocha, c.Axis);
        if (plochaThickness < 1)
            plochaThickness = KolikDoPlochyMm;

        _ = towardB;
    }

    private static bool IsPlocha(DielecModel? d, int contactAxis)
    {
        if (d == null) return false;
        int thin = 0;
        if (d.RozmerY < d.RozmerX) thin = 1;
        if (d.RozmerZ < (thin == 0 ? d.RozmerX : d.RozmerY)) thin = 2;
        return thin == contactAxis;
    }

    private static double MidAlong(DielecModel? d, int axis)
    {
        if (d == null) return 0;
        return axis switch
        {
            0 => d.WcsMinX + d.RozmerX * 0.5,
            1 => d.WcsMinY + d.RozmerY * 0.5,
            _ => d.WcsMinZ + d.RozmerZ * 0.5
        };
    }

    private static double ThicknessAlong(DielecModel? d, int axis)
    {
        if (d == null) return 0;
        return axis switch
        {
            0 => d.RozmerX,
            1 => d.RozmerY,
            _ => d.RozmerZ
        };
    }

    /// <summary>Rovnaké mapovanie ako CNC značenie v SceneBuilder.</summary>
    private static void LocalToFace(DielecModel diel, double lx, double ly, double lz,
        out double faceX, out double faceY, out double faceZ)
    {
        int thick = 0;
        if (diel.RozmerY < diel.RozmerX) thick = 1;
        if (diel.RozmerZ < (thick == 0 ? diel.RozmerX : diel.RozmerY)) thick = 2;

        double[] local = { lx, ly, lz };
        faceZ = local[thick];
        bool first = true;
        faceX = 0;
        faceY = 0;
        for (int i = 0; i < 3; i++)
        {
            if (i == thick) continue;
            if (first) { faceX = local[i]; first = false; }
            else faceY = local[i];
        }
    }
}
