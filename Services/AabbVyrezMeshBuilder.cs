using System.Windows.Media.Media3D;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// 3D mesh = bounding obdĺžnik plochy + výrezy z Excelu (Vyrezy dielca).
/// Face X/Y = poradie AABB osi (ako export); Z hrúbky = najtenší rozmer.
/// </summary>
internal static class AabbVyrezMeshBuilder
{
    private const double Eps = 0.35;

    public static MeshGeometry3D? TryBuild(DielecModel diel, Point3D origin)
    {
        if (diel.RozmerX < 0.1 || diel.RozmerY < 0.1 || diel.RozmerZ < 0.1)
            return null;

        if (!TryBuildFaceProfile(diel, out var outer, out var holes, out double thick))
            return null;

        return Extrude(diel, origin, outer, holes, thick);
    }

    /// <summary>2D obrys v face súradniciach (AABB poradie osi) + diery.</summary>
    public static bool TryBuildFaceProfile(
        DielecModel diel,
        out List<(double X, double Y)> outer,
        out List<List<(double X, double Y)>> holes,
        out double thick)
    {
        outer = new();
        holes = new();
        thick = 0;
        if (diel.RozmerX < 0.1 || diel.RozmerY < 0.1 || diel.RozmerZ < 0.1)
            return false;

        GetFaceFrame(diel, out double faceW, out double faceH, out thick);
        outer = BuildOuterContour(diel, faceW, faceH);
        if (outer.Count < 3)
            return false;
        holes = BuildHoleContours(diel);
        return true;
    }

    public static string Describe(DielecModel diel)
    {
        int nH = diel.Vyrezy.Count(v => v.JeDiera);
        int nE = diel.Vyrezy.Count(v => !v.JeDiera);
        return $"AABB+vyrezy: hranové {nE}, diery {nH}";
    }

    /// <summary>Face rozmery v poradí AABB (a0, a1) + hrúbka.</summary>
    public static void GetFaceFrame(
        DielecModel diel, out double faceW, out double faceH, out double thick)
    {
        double[] d = { diel.RozmerX, diel.RozmerY, diel.RozmerZ };
        int thin = 0;
        if (d[1] < d[0]) thin = 1;
        if (d[2] < d[thin]) thin = 2;
        thick = d[thin];

        int a0 = -1, a1 = -1;
        for (int i = 0; i < 3; i++)
        {
            if (i == thin) continue;
            if (a0 < 0) a0 = i;
            else a1 = i;
        }

        faceW = d[a0];
        faceH = d[a1];
    }

    public static Point3D FaceToLocal(DielecModel diel, double faceX, double faceY, double depth)
    {
        double sx = diel.RozmerX, sy = diel.RozmerY, sz = diel.RozmerZ;
        int thick = 0;
        if (sy < sx) thick = 1;
        if (sz < (thick == 0 ? sx : sy)) thick = 2;

        double x = 0, y = 0, z = 0;
        bool first = true;
        for (int i = 0; i < 3; i++)
        {
            double v;
            if (i == thick) v = depth;
            else if (first) { v = faceX; first = false; }
            else v = faceY;

            if (i == 0) x = v;
            else if (i == 1) y = v;
            else z = v;
        }
        return new Point3D(x, y, z);
    }

    private static List<(double X, double Y)> BuildOuterContour(
        DielecModel diel, double faceW, double faceH)
    {
        // CCW: BL → BR → TR → TL
        var outer = new List<(double X, double Y)>
        {
            (0, 0), (faceW, 0), (faceW, faceH), (0, faceH)
        };

        foreach (var vyrez in diel.Vyrezy.Where(v => !v.JeDiera).OrderBy(v => v.Cislo))
        {
            var notch = GetVyrezPolygon(vyrez);
            if (notch.Count < 3) continue;
            outer = ApplyEdgeNotch(outer, notch, faceW, faceH);
        }

        return SimplifyOrtho(outer);
    }

    private static List<List<(double X, double Y)>> BuildHoleContours(DielecModel diel)
    {
        var holes = new List<List<(double X, double Y)>>();

        foreach (var vyrez in PreferKruhOverRectDiery(diel.Vyrezy.Where(v => v.JeDiera)))
        {
            var poly = GetVyrezPolygon(vyrez);
            if (poly.Count >= 3)
            {
                if (Shoelace(poly) > 0) poly.Reverse(); // diera CW
                holes.Add(SimplifyOrtho(poly));
            }
        }

        // Doplnok z Prieniky, ak Excel Vyrezy diery ešte nemá
        if (holes.Count == 0)
        {
            foreach (var diera in diel.Diery)
            {
                if (!diera.JeDiera)
                    continue;

                LocalToFace(diel, diera.PosX, diera.PosY, diera.PosZ, out double fx, out double fy);
                bool hranata = diera.Typ.Contains("hranat", StringComparison.OrdinalIgnoreCase);
                if (hranata && diera.Sirka >= 8 && diera.Vyska >= 8)
                {
                    double hx = diera.Sirka * 0.5, hy = diera.Vyska * 0.5;
                    holes.Add(new List<(double X, double Y)>
                    {
                        (fx - hx, fy - hy), (fx + hx, fy - hy),
                        (fx + hx, fy + hy), (fx - hx, fy + hy)
                    });
                }
                else if (diera.Priemer >= 15)
                {
                    double r = diera.Priemer * 0.5;
                    var circle = new List<(double X, double Y)>();
                    const int n = 24;
                    for (int i = 0; i < n; i++)
                    {
                        double a = 2 * Math.PI * i / n;
                        circle.Add((fx + r * Math.Cos(a), fy + r * Math.Sin(a)));
                    }
                    if (Shoelace(circle) > 0) circle.Reverse();
                    holes.Add(circle);
                }
            }
        }

        return holes;
    }

    /// <summary>
    /// Excel často dá kruh aj hranatý AABB tej istej diery — nechaj len kruh (alebo len hranatý).
    /// </summary>
    internal static IEnumerable<VyrezModel> PreferKruhOverRectDiery(IEnumerable<VyrezModel> diery)
    {
        var list = diery.OrderBy(v => v.Cislo).ToList();
        var kruhy = list.Where(IsKruhVyrez).ToList();
        foreach (var v in list)
        {
            if (IsKruhVyrez(v))
            {
                yield return v;
                continue;
            }

            bool coveredByKruh = kruhy.Any(k =>
                Math.Abs(k.PosX - v.PosX) < 3
                && Math.Abs(k.PosY - v.PosY) < 3
                && RectOverlapsCircleSize(v.Sirka, v.Vyska, k.Priemer));
            if (coveredByKruh)
                continue;

            yield return v;
        }
    }

    private static bool IsKruhVyrez(VyrezModel v)
        => (v.Tvar ?? "").Contains("kruh", StringComparison.OrdinalIgnoreCase)
           && v.Priemer >= 1;

    private static bool RectOverlapsCircleSize(double sirka, double vyska, double priemer)
    {
        if (priemer < 1) return false;
        return sirka >= priemer - 4 && vyska >= priemer - 4
               && sirka <= priemer + 6 && vyska <= priemer + 6;
    }

    private static List<(double X, double Y)> GetVyrezPolygon(VyrezModel vyrez)
    {
        // Kruh má prioritu pred Body (BREP loop často dá 4 rohy AABB okolo kruhu).
        string tvar = (vyrez.Tvar ?? "").ToLowerInvariant();
        if (tvar.Contains("kruh") && vyrez.Priemer >= 1)
        {
            double r = vyrez.Priemer * 0.5;
            var circle = new List<(double X, double Y)>();
            const int n = 24;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                circle.Add((vyrez.PosX + r * Math.Cos(a), vyrez.PosY + r * Math.Sin(a)));
            }
            return circle;
        }

        if (tvar.Contains("hran") && vyrez.Sirka >= 1 && vyrez.Vyska >= 1)
        {
            // Šírka = face X, Výška = face Y (export musí dodržať osi, nie min/max)
            double hx = vyrez.Sirka * 0.5, hy = vyrez.Vyska * 0.5;
            return new List<(double X, double Y)>
            {
                (vyrez.PosX - hx, vyrez.PosY - hy),
                (vyrez.PosX + hx, vyrez.PosY - hy),
                (vyrez.PosX + hx, vyrez.PosY + hy),
                (vyrez.PosX - hx, vyrez.PosY + hy),
            };
        }

        if (vyrez.Body.Count >= 3)
        {
            return vyrez.Body
                .OrderBy(b => b.Cislo)
                .Select(b => (b.PosX, b.PosY))
                .ToList();
        }

        return new List<(double X, double Y)>();
    }

    /// <summary>
    /// Vloží ortogonálny výrez od hrany do vonkajšieho obrysu.
    /// </summary>
    private static List<(double X, double Y)> ApplyEdgeNotch(
        List<(double X, double Y)> outer,
        List<(double X, double Y)> notch,
        double faceW,
        double faceH)
    {
        var nsimp = SimplifyOrtho(notch);
        if (nsimp.Count < 3) return outer;

        // Ústie výrezu = body na AABB, ktoré susedia s vnútorným bodom.
        // Otvorený polygón (Excel U bez návratu): nesmie sa wrap-ovať last→first.
        var edges = nsimp.Select(p => BorderEdge(p, faceW, faceH)).ToList();
        bool openPoly = edges[0] >= 0 && edges[^1] < 0;

        int iLeave = -1, iEnter = -1;
        int last = openPoly ? nsimp.Count - 1 : nsimp.Count;
        for (int i = 0; i < last; i++)
        {
            int j = openPoly ? i + 1 : (i + 1) % nsimp.Count;
            if (j >= nsimp.Count) break;
            if (edges[i] >= 0 && edges[j] < 0) iLeave = i;
            if (edges[i] < 0 && edges[j] >= 0) iEnter = j;
        }

        // Otvorený U z Excelu: začína na hrane, ide dnu, chýba návrat na hranu
        // (napr. (567,332)→(537,332)→(537,402) bez (567,402)).
        if (openPoly || (iLeave >= 0 && (iEnter < 0 || iEnter == iLeave)))
        {
            if (iLeave < 0)
            {
                // prvý bod na hrane
                for (int i = 0; i < edges.Count; i++)
                {
                    if (edges[i] >= 0) { iLeave = i; break; }
                }
            }

            if (iLeave >= 0)
            {
                int eOpen = edges[iLeave];
                var openPath = new List<(double X, double Y)> { nsimp[iLeave] };
                for (int k = iLeave + 1; k < nsimp.Count; k++)
                {
                    if (edges[k] >= 0)
                        break;
                    openPath.Add(nsimp[k]);
                }

                if (openPath.Count >= 2)
                {
                    var back = ProjectToBorder(openPath[^1], eOpen, faceW, faceH);
                    if (Dist(back, openPath[0]) > Eps && Dist(back, openPath[^1]) > Eps)
                        openPath.Add(back);

                    if (openPath.Count >= 3)
                    {
                        if (!PathGoesInward(openPath, eOpen, faceW, faceH))
                            openPath.Reverse();
                        return InsertPathOnEdge(outer, openPath, eOpen, faceW, faceH);
                    }
                }
            }
        }

        if (iLeave < 0 || iEnter < 0) return outer;

        // cesta: leave → interior… → enter
        var path = new List<(double X, double Y)>();
        for (int k = iLeave; ; k = (k + 1) % nsimp.Count)
        {
            path.Add(nsimp[k]);
            if (k == iEnter) break;
            if (path.Count > nsimp.Count + 2) return outer;
        }

        if (path.Count < 2) return outer;

        int eA = edges[iLeave];
        int eB = edges[iEnter];

        // Rovnaká hrana = U výrez; susedné hrany = roh L
        if (eA == eB)
        {
            if (!PathGoesInward(path, eA, faceW, faceH))
                path.Reverse();
            return InsertPathOnEdge(outer, path, eA, faceW, faceH);
        }

        if (IsAdjacentEdge(eA, eB))
            return InsertCornerPath(outer, path, eA, eB, faceW, faceH);

        return outer;
    }

    private static bool IsAdjacentEdge(int e0, int e1)
    {
        int d = Math.Abs(e0 - e1);
        return d == 1 || d == 3;
    }

    /// <summary>L-výrez cez roh: path spája dve susedné hrany v smere CCW.</summary>
    private static List<(double X, double Y)> InsertCornerPath(
        List<(double X, double Y)> outer,
        List<(double X, double Y)> path,
        int edgeStart,
        int edgeEnd,
        double w,
        double h)
    {
        int next = (edgeStart + 1) % 4;
        int prev = (edgeStart + 3) % 4;
        if (edgeEnd == prev)
        {
            path = path.ToList();
            path.Reverse();
            (edgeStart, edgeEnd) = (edgeEnd, edgeStart);
        }
        else if (edgeEnd != next)
        {
            return outer;
        }

        if (path.Count >= 2 && BorderEdge(path[0], w, h) != edgeStart
            && !(IsCorner(path[0], w, h) && TouchesEdge(path[0], edgeStart, w, h)))
        {
            path = path.ToList();
            path.Reverse();
        }

        double ParamOn((double X, double Y) p, int edge) => edge switch
        {
            0 => p.X,
            1 => p.Y,
            2 => w - p.X,
            3 => h - p.Y,
            _ => 0
        };

        double pStart = ParamOn(ClampToEdge(path[0], edgeStart, w, h), edgeStart);
        double pEnd = ParamOn(ClampToEdge(path[^1], edgeEnd, w, h), edgeEnd);

        var result = new List<(double X, double Y)>();
        bool inserted = false;
        bool skipping = false;

        for (int i = 0; i < outer.Count; i++)
        {
            var cur = outer[i];
            var nxt = outer[(i + 1) % outer.Count];

            if (skipping)
            {
                int eCur = BorderEdge(cur, w, h);
                if (eCur == edgeEnd || (IsCorner(cur, w, h) && TouchesEdge(cur, edgeEnd, w, h)))
                {
                    double t = ParamOn(ClampToEdge(cur, edgeEnd, w, h), edgeEnd);
                    if (t + Eps >= pEnd)
                    {
                        skipping = false;
                        // path už skončila na edgeEnd — nepridávaj duplicitný cur ak je to koniec path
                        if (Dist(cur, path[^1]) > Eps)
                            result.Add(cur);
                    }
                }
                continue;
            }

            if (!inserted)
            {
                bool onStartSeg =
                    (BorderEdge(cur, w, h) == edgeStart || (IsCorner(cur, w, h) && TouchesEdge(cur, edgeStart, w, h)))
                    && (BorderEdge(nxt, w, h) == edgeStart || IsCorner(nxt, w, h) || BorderEdge(nxt, w, h) == edgeEnd);

                if (onStartSeg)
                {
                    double ta = ParamOn(ClampToEdge(cur, edgeStart, w, h), edgeStart);
                    double tb = ParamOn(ClampToEdge(
                        BorderEdge(nxt, w, h) == edgeStart || IsCorner(nxt, w, h)
                            ? nxt
                            : ClampToEdge(nxt, edgeStart, w, h),
                        edgeStart, w, h), edgeStart);

                    if (tb + Eps >= ta && ta <= pStart + Eps && tb >= pStart - Eps)
                    {
                        result.Add(cur);
                        foreach (var pt in path)
                            result.Add(pt);
                        inserted = true;
                        skipping = true;
                        continue;
                    }
                }
            }

            result.Add(cur);
        }

        return inserted ? DedupNear(result) : outer;
    }

    private static bool TouchesEdge((double X, double Y) p, int edge, double w, double h)
        => edge switch
        {
            0 => Near(p.Y, 0),
            1 => Near(p.X, w),
            2 => Near(p.Y, h),
            3 => Near(p.X, 0),
            _ => false
        };

    private static int BorderEdge((double X, double Y) p, double w, double h)
    {
        if (Near(p.Y, 0) && p.X >= -Eps && p.X <= w + Eps) return 0; // bottom
        if (Near(p.X, w) && p.Y >= -Eps && p.Y <= h + Eps) return 1; // right
        if (Near(p.Y, h) && p.X >= -Eps && p.X <= w + Eps) return 2; // top
        if (Near(p.X, 0) && p.Y >= -Eps && p.Y <= h + Eps) return 3; // left
        return -1;
    }

    private static (double X, double Y) ProjectToBorder((double X, double Y) p, int edge, double w, double h)
        => edge switch
        {
            0 => (Clamp(p.X, 0, w), 0),
            1 => (w, Clamp(p.Y, 0, h)),
            2 => (Clamp(p.X, 0, w), h),
            3 => (0, Clamp(p.Y, 0, h)),
            _ => p
        };

    private static bool PathGoesInward(
        List<(double X, double Y)> path, int edge, double w, double h)
    {
        if (path.Count < 2) return true;
        var mid = path[path.Count / 2];
        return edge switch
        {
            0 => mid.Y > Eps,          // bottom → inward = +Y
            1 => mid.X < w - Eps,      // right → inward = -X
            2 => mid.Y < h - Eps,      // top → inward = -Y
            3 => mid.X > Eps,          // left → inward = +X
            _ => true
        };
    }

    private static List<(double X, double Y)> InsertPathOnEdge(
        List<(double X, double Y)> outer,
        List<(double X, double Y)> path,
        int edge,
        double w,
        double h)
    {
        // Parameter pozdĺž hrany v smere CCW
        double Param((double X, double Y) p) => edge switch
        {
            0 => p.X,           // bottom L→R
            1 => p.Y,           // right B→T
            2 => w - p.X,       // top R→L
            3 => h - p.Y,       // left T→B
            _ => 0
        };

        double p0 = Param(path[0]);
        double p1 = Param(path[^1]);
        bool swapEnds = p0 > p1 + Eps;
        if (swapEnds)
        {
            path = path.ToList();
            path.Reverse();
            (p0, p1) = (p1, p0);
        }

        // Nájdi segment outer na tejto hrane, ktorý pokrýva [p0,p1]
        var result = new List<(double X, double Y)>();
        for (int i = 0; i < outer.Count; i++)
        {
            var a = outer[i];
            var b = outer[(i + 1) % outer.Count];
            int eA = BorderEdge(a, w, h);
            int eB = BorderEdge(b, w, h);

            // segment na cieľovej hrane (obe body na edge, alebo roh→edge)
            bool onEdge = (eA == edge && eB == edge)
                          || (eA == edge && IsCorner(b, w, h))
                          || (eB == edge && IsCorner(a, w, h))
                          || (IsCorner(a, w, h) && IsCorner(b, w, h) && EdgeBetweenCorners(a, b, edge, w, h));

            if (!onEdge || BorderEdge(a, w, h) < 0 && BorderEdge(b, w, h) < 0)
            {
                result.Add(a);
                continue;
            }

            // Ak tento segment leží na edge a prekrýva notch
            if (eA == edge || eB == edge || EdgeBetweenCorners(a, b, edge, w, h))
            {
                double ta = Param(ClampToEdge(a, edge, w, h));
                double tb = Param(ClampToEdge(b, edge, w, h));
                double lo = Math.Min(ta, tb), hi = Math.Max(ta, tb);
                if (hi < p0 - Eps || lo > p1 + Eps)
                {
                    result.Add(a);
                    continue;
                }

                // ideme v smere CCW: ta → tb by malo byť rostúce param
                if (tb + Eps < ta)
                {
                    result.Add(a);
                    continue; // opačný smer na tej istej hrane (nemalo by nastať pri CCW)
                }

                result.Add(a);
                // vložiť path namiesto úseku [p0,p1]
                if (ta <= p0 + Eps && tb >= p1 - Eps)
                {
                    foreach (var pt in path)
                        result.Add(pt);
                    // b pridá ďalšia iterácia / na konci
                    continue;
                }
            }

            result.Add(a);
        }

        // Fallback: ak sa nepodarilo vložiť (duplicitný výsledok ≈ outer), skús priamo
        if (result.Count <= outer.Count + 1)
        {
            return InsertPathBrute(outer, path, edge, w, h, p0, p1);
        }

        return DedupNear(result);
    }

    private static List<(double X, double Y)> InsertPathBrute(
        List<(double X, double Y)> outer,
        List<(double X, double Y)> path,
        int edge,
        double w,
        double h,
        double p0,
        double p1)
    {
        double Param((double X, double Y) p) => edge switch
        {
            0 => p.X,
            1 => p.Y,
            2 => w - p.X,
            3 => h - p.Y,
            _ => 0
        };

        var result = new List<(double X, double Y)>();
        bool inserted = false;
        for (int i = 0; i < outer.Count; i++)
        {
            var a = outer[i];
            var b = outer[(i + 1) % outer.Count];
            result.Add(a);

            if (inserted) continue;
            if (BorderEdge(a, w, h) != edge && !IsCorner(a, w, h)) continue;
            if (BorderEdge(b, w, h) != edge && !IsCorner(b, w, h)) continue;

            double ta = Param(ClampToEdge(a, edge, w, h));
            double tb = Param(ClampToEdge(b, edge, w, h));
            if (tb + Eps < ta) continue;
            if (ta <= p0 + Eps && tb >= p1 - Eps)
            {
                foreach (var pt in path)
                    result.Add(pt);
                inserted = true;
            }
        }

        return inserted ? DedupNear(result) : outer;
    }

    private static bool IsCorner((double X, double Y) p, double w, double h)
        => (Near(p.X, 0) || Near(p.X, w)) && (Near(p.Y, 0) || Near(p.Y, h));

    private static bool EdgeBetweenCorners(
        (double X, double Y) a, (double X, double Y) b, int edge, double w, double h)
    {
        if (!IsCorner(a, w, h) || !IsCorner(b, w, h)) return false;
        return edge switch
        {
            0 => Near(a.Y, 0) && Near(b.Y, 0),
            1 => Near(a.X, w) && Near(b.X, w),
            2 => Near(a.Y, h) && Near(b.Y, h),
            3 => Near(a.X, 0) && Near(b.X, 0),
            _ => false
        };
    }

    private static (double X, double Y) ClampToEdge((double X, double Y) p, int edge, double w, double h)
        => edge switch
        {
            0 => (Clamp(p.X, 0, w), 0),
            1 => (w, Clamp(p.Y, 0, h)),
            2 => (Clamp(p.X, 0, w), h),
            3 => (0, Clamp(p.Y, 0, h)),
            _ => p
        };

    private static double Clamp(double v, double lo, double hi)
        => v < lo ? lo : (v > hi ? hi : v);

    private static MeshGeometry3D Extrude(
        DielecModel diel,
        Point3D origin,
        List<(double X, double Y)> outer,
        List<List<(double X, double Y)>> holes,
        double thick)
    {
        var mesh = new MeshGeometry3D();

        // Front / back
        AddFaceTriangles(mesh, diel, origin, outer, holes, depth: 0, flip: false);
        AddFaceTriangles(mesh, diel, origin, outer, holes, depth: thick, flip: true);

        // Side walls outer
        for (int i = 0; i < outer.Count; i++)
        {
            var a = outer[i];
            var b = outer[(i + 1) % outer.Count];
            AddWall(mesh, diel, origin, a, b, thick);
        }

        // Hole walls
        foreach (var hole in holes)
        {
            for (int i = 0; i < hole.Count; i++)
            {
                var a = hole[i];
                var b = hole[(i + 1) % hole.Count];
                AddWall(mesh, diel, origin, a, b, thick);
            }
        }

        mesh.Freeze();
        return mesh;
    }

    private static void AddFaceTriangles(
        MeshGeometry3D mesh,
        DielecModel diel,
        Point3D origin,
        List<(double X, double Y)> outer,
        List<List<(double X, double Y)>> holes,
        double depth,
        bool flip)
    {
        // Ortogonálny grid (spoľahlivé pri dierach); bez dier ear clipping
        var tris = TriangulatePoints(outer, holes);
        if (tris.Count == 0) return;

        int baseIdx = mesh.Positions.Count;
        var posIndex = new Dictionary<(long, long), int>();
        int AddVert((double X, double Y) p)
        {
            long kx = (long)Math.Round(p.X * 100);
            long ky = (long)Math.Round(p.Y * 100);
            var key = (kx, ky);
            if (posIndex.TryGetValue(key, out int existing))
                return existing;
            int idx = mesh.Positions.Count;
            mesh.Positions.Add(ToWorld(diel, origin, p.X, p.Y, depth));
            posIndex[key] = idx;
            return idx;
        }

        foreach (var (a, b, c) in tris)
        {
            int ia = AddVert(a);
            int ib = AddVert(b);
            int ic = AddVert(c);
            if (!flip)
            {
                mesh.TriangleIndices.Add(ia);
                mesh.TriangleIndices.Add(ib);
                mesh.TriangleIndices.Add(ic);
            }
            else
            {
                mesh.TriangleIndices.Add(ia);
                mesh.TriangleIndices.Add(ic);
                mesh.TriangleIndices.Add(ib);
            }
        }
    }

    /// <summary>
    /// Triangulácia: bez dier = ear clipping; s dierami = orto mriežka buniek mimo dier.
    /// </summary>
    private static List<((double X, double Y) A, (double X, double Y) B, (double X, double Y) C)> TriangulatePoints(
        List<(double X, double Y)> outer,
        List<List<(double X, double Y)>> holes)
    {
        if (holes.Count == 0)
        {
            var idxTris = EarClip(
                outer.ToList(),
                Enumerable.Range(0, outer.Count).ToList());
            return idxTris
                .Select(t => (outer[t.A], outer[t.B], outer[t.C]))
                .ToList();
        }

        return TriangulateOrthoWithHoles(outer, holes);
    }

    /// <summary>
    /// Rozdelí bounding box na mriežku podľa X/Y hrán obrysu aj dier; bunky mimo materiálu vyhodí.
    /// </summary>
    private static List<((double X, double Y) A, (double X, double Y) B, (double X, double Y) C)> TriangulateOrthoWithHoles(
        List<(double X, double Y)> outer,
        List<List<(double X, double Y)>> holes)
    {
        var result = new List<((double X, double Y) A, (double X, double Y) B, (double X, double Y) C)>();

        var xs = new SortedSet<double>();
        var ys = new SortedSet<double>();
        foreach (var p in outer)
        {
            xs.Add(Math.Round(p.X, 3));
            ys.Add(Math.Round(p.Y, 3));
        }
        foreach (var h in holes)
        {
            foreach (var p in h)
            {
                xs.Add(Math.Round(p.X, 3));
                ys.Add(Math.Round(p.Y, 3));
            }
        }

        var xv = xs.ToList();
        var yv = ys.ToList();
        if (xv.Count < 2 || yv.Count < 2)
            return result;

        for (int i = 0; i < xv.Count - 1; i++)
        {
            for (int j = 0; j < yv.Count - 1; j++)
            {
                double x0 = xv[i], x1 = xv[i + 1];
                double y0 = yv[j], y1 = yv[j + 1];
                if (x1 - x0 < 0.05 || y1 - y0 < 0.05)
                    continue;

                var mid = ((x0 + x1) * 0.5, (y0 + y1) * 0.5);
                if (!PointInPoly(mid, outer))
                    continue;
                if (holes.Any(h => PointInPoly(mid, h)))
                    continue;

                var bl = (x0, y0);
                var br = (x1, y0);
                var tr = (x1, y1);
                var tl = (x0, y1);
                // CCW
                result.Add((bl, br, tr));
                result.Add((bl, tr, tl));
            }
        }

        return result;
    }

    /// <summary>
    /// Legacy indexová triangulácia (len bez dier / testy).
    /// </summary>
    private static List<(int A, int B, int C)> Triangulate(
        List<(double X, double Y)> outer,
        List<List<(double X, double Y)>> holes)
    {
        if (holes.Count > 0)
        {
            // mapovanie bodov na indexy nie je 1:1 s gridom — len empty
            return new List<(int A, int B, int C)>();
        }

        return EarClip(outer.ToList(), Enumerable.Range(0, outer.Count).ToList());
    }

    private static int FindBridgeVertex(
        List<(double X, double Y)> poly, (double X, double Y) from)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            if (poly[i].X + Eps < from.X) continue; // most doprava od diery
            double d = Dist(poly[i], from);
            if (d >= bestD) continue;
            // segment mosta nesmie pretínať hranu poly (okrem konca)
            if (BridgeCrosses(poly, i, from)) continue;
            bestD = d;
            best = i;
        }

        if (best >= 0) return best;

        // fallback: najbližší bod vpravo / akýkoľvek
        for (int i = 0; i < poly.Count; i++)
        {
            double d = Dist(poly[i], from);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static bool BridgeCrosses(
        List<(double X, double Y)> poly, int oi, (double X, double Y) from)
    {
        var a = from;
        var b = poly[oi];
        for (int i = 0; i < poly.Count; i++)
        {
            int j = (i + 1) % poly.Count;
            if (i == oi || j == oi) continue;
            if (SegmentsIntersect(a, b, poly[i], poly[j], touchOk: true))
                return true;
        }
        return false;
    }

    private static List<(int A, int B, int C)> EarClip(
        List<(double X, double Y)> poly,
        List<int> indexMap)
    {
        var tris = new List<(int A, int B, int C)>();
        if (poly.Count < 3) return tris;

        var verts = poly.ToList();
        var idx = indexMap.ToList();

        // zabezpeč CCW
        if (Shoelace(verts) < 0)
        {
            verts.Reverse();
            idx.Reverse();
        }

        int guard = 0;
        while (verts.Count > 3 && guard++ < 10000)
        {
            bool clipped = false;
            for (int i = 0; i < verts.Count; i++)
            {
                int i0 = (i - 1 + verts.Count) % verts.Count;
                int i1 = i;
                int i2 = (i + 1) % verts.Count;
                var a = verts[i0];
                var b = verts[i1];
                var c = verts[i2];

                if (!IsConvex(a, b, c)) continue;
                if (EarContainsPoint(verts, i0, i1, i2)) continue;

                tris.Add((idx[i0], idx[i1], idx[i2]));
                verts.RemoveAt(i1);
                idx.RemoveAt(i1);
                clipped = true;
                break;
            }
            if (!clipped) break;
        }

        if (verts.Count == 3)
            tris.Add((idx[0], idx[1], idx[2]));

        return tris;
    }

    private static bool IsConvex(
        (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        => Cross(a, b, c) > Eps; // CCW turn

    private static double Cross(
        (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool EarContainsPoint(
        List<(double X, double Y)> verts, int i0, int i1, int i2)
    {
        var a = verts[i0];
        var b = verts[i1];
        var c = verts[i2];
        for (int i = 0; i < verts.Count; i++)
        {
            if (i == i0 || i == i1 || i == i2) continue;
            if (PointInTriangle(verts[i], a, b, c))
                return true;
        }
        return false;
    }

    private static bool PointInTriangle(
        (double X, double Y) p,
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c)
    {
        double c1 = Cross(a, b, p);
        double c2 = Cross(b, c, p);
        double c3 = Cross(c, a, p);
        bool hasNeg = c1 < -Eps || c2 < -Eps || c3 < -Eps;
        bool hasPos = c1 > Eps || c2 > Eps || c3 > Eps;
        return !(hasNeg && hasPos);
    }

    private static bool SegmentsIntersect(
        (double X, double Y) a, (double X, double Y) b,
        (double X, double Y) c, (double X, double Y) d,
        bool touchOk)
    {
        double d1 = Cross(a, b, c);
        double d2 = Cross(a, b, d);
        double d3 = Cross(c, d, a);
        double d4 = Cross(c, d, b);
        if (touchOk)
        {
            if (Math.Abs(d1) <= Eps || Math.Abs(d2) <= Eps || Math.Abs(d3) <= Eps || Math.Abs(d4) <= Eps)
                return false;
        }
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    private static void AddWall(
        MeshGeometry3D mesh,
        DielecModel diel,
        Point3D origin,
        (double X, double Y) a,
        (double X, double Y) b,
        double thick)
    {
        int i0 = mesh.Positions.Count;
        mesh.Positions.Add(ToWorld(diel, origin, a.X, a.Y, 0));
        mesh.Positions.Add(ToWorld(diel, origin, b.X, b.Y, 0));
        mesh.Positions.Add(ToWorld(diel, origin, b.X, b.Y, thick));
        mesh.Positions.Add(ToWorld(diel, origin, a.X, a.Y, thick));
        mesh.TriangleIndices.Add(i0);
        mesh.TriangleIndices.Add(i0 + 1);
        mesh.TriangleIndices.Add(i0 + 2);
        mesh.TriangleIndices.Add(i0);
        mesh.TriangleIndices.Add(i0 + 2);
        mesh.TriangleIndices.Add(i0 + 3);
    }

    private static Point3D ToWorld(
        DielecModel diel, Point3D origin, double fx, double fy, double depth)
    {
        var local = FaceToLocal(diel, fx, fy, depth);
        return new Point3D(origin.X + local.X, origin.Y + local.Y, origin.Z + local.Z);
    }

    private static void LocalToFace(
        DielecModel diel, double lx, double ly, double lz, out double fx, out double fy)
    {
        double[] d = { diel.RozmerX, diel.RozmerY, diel.RozmerZ };
        double[] c = { lx, ly, lz };
        int thin = 0;
        if (d[1] < d[0]) thin = 1;
        if (d[2] < d[thin]) thin = 2;
        int a0 = -1, a1 = -1;
        for (int i = 0; i < 3; i++)
        {
            if (i == thin) continue;
            if (a0 < 0) a0 = i;
            else a1 = i;
        }
        fx = c[a0];
        fy = c[a1];
    }

    private static List<(double X, double Y)> SimplifyOrtho(List<(double X, double Y)> pts)
    {
        if (pts.Count < 3) return pts.ToList();
        var clean = DedupNear(pts);
        if (clean.Count > 1 && Dist(clean[0], clean[^1]) < Eps)
            clean.RemoveAt(clean.Count - 1);

        var result = new List<(double X, double Y)>();
        for (int i = 0; i < clean.Count; i++)
        {
            var prev = clean[(i - 1 + clean.Count) % clean.Count];
            var cur = clean[i];
            var next = clean[(i + 1) % clean.Count];
            bool colinear =
                (Near(prev.X, cur.X) && Near(cur.X, next.X))
                || (Near(prev.Y, cur.Y) && Near(cur.Y, next.Y));
            if (!colinear)
                result.Add(cur);
        }
        return result.Count >= 3 ? result : clean;
    }

    private static List<(double X, double Y)> DedupNear(List<(double X, double Y)> pts)
    {
        var r = new List<(double X, double Y)>();
        foreach (var p in pts)
        {
            if (r.Count > 0 && Dist(r[^1], p) < Eps) continue;
            r.Add(p);
        }
        if (r.Count > 1 && Dist(r[0], r[^1]) < Eps)
            r.RemoveAt(r.Count - 1);
        return r;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static bool Near(double a, double b) => Math.Abs(a - b) <= Eps;

    private static double Shoelace(List<(double X, double Y)> poly)
    {
        double s = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s * 0.5;
    }

    private static bool PointInPoly((double X, double Y) p, List<(double X, double Y)> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if (((a.Y > p.Y) != (b.Y > p.Y))
                && (p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y + 1e-30) + a.X))
                inside = !inside;
        }
        return inside;
    }
}
