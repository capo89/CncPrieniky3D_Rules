using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// 2D obrys dielca (a vnútorné diery) v súradniciach workpiece (Z = hrúbka).
/// </summary>
internal static class VyrezProfileBuilder
{
    private const double Eps = 0.08;

    public sealed record Profile(
        List<List<(double X, double Y)>> BorderCutouts,
        List<List<(double X, double Y)>> Holes);

    public static Profile Build(DielecModel diel)
    {
        var fromExcel = BuildFromExcelVyrezy(diel);
        if (fromExcel != null)
            return fromExcel;

        if (diel.Body.Count < 4)
        {
            var only = new List<List<(double X, double Y)>>();
            AppendHolesFromPrieniky(diel, only);
            return new Profile(new(), only);
        }

        var pts2 = new List<(double X, double Y)>();
        foreach (var b in diel.Body)
        {
            LocalAabbToWorkpieceXY(diel, b.PosX, b.PosY, b.PosZ, out double x, out double y);
            var p = (X: Round2(x), Y: Round2(y));
            if (!pts2.Any(q => Near(q.X, p.X) && Near(q.Y, p.Y)))
                pts2.Add(p);
        }

        if (pts2.Count < 3)
        {
            var only = new List<List<(double X, double Y)>>();
            AppendHolesFromPrieniky(diel, only);
            return new Profile(new(), only);
        }

        var edges = BuildOrthoEdges(pts2);
        RemoveBorderNotchChords(pts2, edges);
        var cycles = FindCycles(edges)
            .Where(c => c.Count >= 3 && Math.Abs(Shoelace(c)) > 1.0)
            .Select(c => c.Select(p => (X: Round2(p.X), Y: Round2(p.Y))).ToList())
            .GroupBy(CycleSignature)
            .Select(g => g.First())
            .OrderByDescending(c => Math.Abs(Shoelace(c)))
            .ToList();

        if (cycles.Count == 0)
        {
            var only = new List<List<(double X, double Y)>>();
            AppendHolesFromPrieniky(diel, only);
            return new Profile(new(), only);
        }

        var outer = cycles[0];
        if (Shoelace(outer) < 0) outer.Reverse();

        var holes = new List<List<(double X, double Y)>>();
        for (int i = 1; i < cycles.Count; i++)
        {
            var h = cycles[i];
            var mid = (X: h.Average(p => p.X), Y: h.Average(p => p.Y));
            if (!PointInPoly(mid, outer)) continue;
            if (Shoelace(h) > 0) h.Reverse(); // diera CW
            holes.Add(h);
        }

        // Čistý obdĺžnik → žiadny zafrez. Inak len U/L zafrezy (nie celý obvod).
        var cutouts = new List<List<(double X, double Y)>>();
        if (!IsAxisAlignedRectangleMatchingWorkpiece(diel, outer))
        {
            var (dx, dy, _) = PartRules.For(diel).WorkpieceSize(diel);
            cutouts.AddRange(ExtractBorderCutouts(outer, dx, dy));
        }

        AppendHolesFromPrieniky(diel, holes);
        return new Profile(cutouts, holes);
    }

    /// <summary>
    /// Bounding obdĺžnik + Vyrezy dielca → cutouts/holes v workpiece X/Y.
    /// </summary>
    private static Profile? BuildFromExcelVyrezy(DielecModel diel)
    {
        if (!AabbVyrezMeshBuilder.TryBuildFaceProfile(diel, out var outerFace, out var holesFace, out _))
            return null;

        var outerWp = outerFace.Select(p => FaceToWorkpiece(diel, p.X, p.Y)).Select(p => (Round2(p.X), Round2(p.Y))).ToList();
        outerWp = Dedup2(outerWp);
        if (outerWp.Count < 3)
            return null;
        if (Shoelace(outerWp) < 0) outerWp.Reverse();

        var (dx, dy, _) = PartRules.For(diel).WorkpieceSize(diel);
        var cutouts = new List<List<(double X, double Y)>>();
        if (!IsAxisAlignedRectangleMatchingWorkpiece(diel, outerWp))
            cutouts.AddRange(ExtractBorderCutouts(outerWp, dx, dy));

        var holes = new List<List<(double X, double Y)>>();
        foreach (var h in holesFace)
        {
            var hw = h.Select(p => FaceToWorkpiece(diel, p.X, p.Y)).Select(p => (Round2(p.X), Round2(p.Y))).ToList();
            hw = Dedup2(hw);
            if (hw.Count < 3) continue;
            if (Shoelace(hw) > 0) hw.Reverse();
            holes.Add(hw);
        }

        AppendHolesFromPrieniky(diel, holes);
        return new Profile(cutouts, holes);
    }

    private static (double X, double Y) FaceToWorkpiece(DielecModel diel, double faceX, double faceY)
    {
        // Face = AABB poradie; swap len pri otočení kvôli Y>1350 (PartRules).
        if (PartRules.WorkpieceSwapsFaceAxes(diel))
            return (faceY, faceX);
        return (faceX, faceY);
    }

    private static List<(double X, double Y)> Dedup2(List<(double X, double Y)> pts)
    {
        var r = new List<(double X, double Y)>();
        foreach (var p in pts)
        {
            if (r.Any(q => Near(q.X, p.X) && Near(q.Y, p.Y))) continue;
            r.Add(p);
        }
        return r;
    }

    /// <summary>
    /// Diery z listu Prieniky (kruh / hranatý) — Body BREP často nemá dosť bodov na cyklus.
    /// </summary>
    private static void AppendHolesFromPrieniky(
        DielecModel diel, List<List<(double X, double Y)>> holes)
    {
        foreach (var diera in diel.Diery)
        {
            if (!diera.JeDiera)
                continue;

            LocalAabbToWorkpieceXY(diel, diera.PosX, diera.PosY, diera.PosZ,
                out double cx, out double cy);

            List<(double X, double Y)>? poly = null;
            bool hranata = diera.Typ.Contains("hranat", StringComparison.OrdinalIgnoreCase);
            if (hranata && diera.Sirka >= 8 && diera.Vyska >= 8)
            {
                double hx = diera.Sirka * 0.5, hy = diera.Vyska * 0.5;
                poly = new List<(double X, double Y)>
                {
                    (Round2(cx - hx), Round2(cy - hy)),
                    (Round2(cx + hx), Round2(cy - hy)),
                    (Round2(cx + hx), Round2(cy + hy)),
                    (Round2(cx - hx), Round2(cy + hy)),
                };
            }
            else if (diera.Priemer >= 15)
            {
                double r = diera.Priemer * 0.5;
                poly = new List<(double X, double Y)>();
                const int n = 24;
                for (int i = 0; i < n; i++)
                {
                    double a = 2 * Math.PI * i / n;
                    poly.Add((Round2(cx + r * Math.Cos(a)), Round2(cy + r * Math.Sin(a))));
                }
            }

            if (poly == null) continue;

            var mid = (X: poly.Average(p => p.X), Y: poly.Average(p => p.Y));
            bool already = holes.Any(h =>
            {
                var m = (X: h.Average(p => p.X), Y: h.Average(p => p.Y));
                return Math.Abs(m.X - mid.X) < 2 && Math.Abs(m.Y - mid.Y) < 2;
            });
            if (already) continue;

            if (Shoelace(poly) > 0) poly.Reverse();
            holes.Add(poly);
        }
    }

    /// <summary>
    /// Vnútorný výrez: stred dole (Ymin) → L-dole → … → P-dole → stred dole.
    /// </summary>
    public static List<(double X, double Y)> InteriorEntryPath(List<(double X, double Y)> hole)
    {
        if (hole.Count < 3)
            return hole.ToList();

        double minY = hole.Min(p => p.Y);
        var botPts = hole.Where(p => Near(p.Y, minY)).OrderBy(p => p.X).ToList();
        if (botPts.Count == 0)
            return hole.ToList();

        double xMinBot = botPts[0].X;
        double xMaxBot = botPts[^1].X;
        var start = (X: (xMinBot + xMaxBot) * 0.5, Y: minY);

        int iBotL = IndexOfNear(hole, xMinBot, minY);
        int iBotR = IndexOfNear(hole, xMaxBot, minY);
        if (iBotL < 0) iBotL = 0;
        if (iBotR < 0) iBotR = 0;

        var path = new List<(double X, double Y)> { start, hole[iBotL] };

        int n = hole.Count;
        int nextCw = (iBotL + 1) % n;
        int nextCcw = (iBotL - 1 + n) % n;
        // Preferuj smer, ktorý ide nahor (väčšie Y); inak vyhni sa dolnej hrane doprava.
        int dir;
        if (hole[nextCw].Y > hole[iBotL].Y + Eps) dir = 1;
        else if (hole[nextCcw].Y > hole[iBotL].Y + Eps) dir = -1;
        else if (Near(hole[nextCw].X, xMaxBot) && Near(hole[nextCw].Y, minY)) dir = -1;
        else dir = 1;

        int cur = iBotL;
        for (int step = 0; step < n; step++)
        {
            cur = (cur + dir + n) % n;
            path.Add(hole[cur]);
            if (Near(hole[cur].X, xMaxBot) && Near(hole[cur].Y, minY))
                break;
        }

        path.Add(start);
        return DedupConsecutive(path);
    }

    /// <summary>Uzavretý vonkajší obrys — legacy, už sa do XCS neexportuje celý.</summary>
    public static List<(double X, double Y)> OuterPath(List<(double X, double Y)> outer)
    {
        if (outer.Count == 0) return outer;
        int start = 0;
        for (int i = 1; i < outer.Count; i++)
        {
            var p = outer[i];
            var s = outer[start];
            if (p.Y < s.Y - Eps || (Near(p.Y, s.Y) && p.X < s.X))
                start = i;
        }

        var path = new List<(double X, double Y)>();
        for (int i = 0; i < outer.Count; i++)
            path.Add(outer[(start + i) % outer.Count]);
        return path;
    }

    /// <summary>
    /// Z vonkajšieho obrysu vytiahne len U/L zafrezy na kraji AABB (nie celý obdĺžnik).
    /// U: vstup a výstup na tej istej strane. L: vstup a výstup na susedných stranách (+ roh AABB).
    /// </summary>
    public static List<List<(double X, double Y)>> ExtractBorderCutouts(
        List<(double X, double Y)> outer, double dx, double dy)
    {
        var result = new List<List<(double X, double Y)>>();
        if (outer.Count < 3) return result;

        int n = outer.Count;
        // Indexy bodov, ktoré nie sú na obvode AABB (vnútorné „dno“ zafrezu)
        bool Inside((double X, double Y) p) =>
            p.X > Eps * 2 && p.X < dx - Eps * 2 && p.Y > Eps * 2 && p.Y < dy - Eps * 2;

        int BorderId((double X, double Y) p)
        {
            // 0=bottom, 1=right, 2=top, 3=left; -1 = nie na hranici / roh (viac strán)
            bool b = Near(p.Y, 0) || p.Y < Eps;
            bool r = Near(p.X, dx) || p.X > dx - Eps;
            bool t = Near(p.Y, dy) || p.Y > dy - Eps;
            bool l = Near(p.X, 0) || p.X < Eps;
            int c = (b ? 1 : 0) + (r ? 1 : 0) + (t ? 1 : 0) + (l ? 1 : 0);
            if (c != 1) return -1; // roh alebo vnútro
            if (b) return 0;
            if (r) return 1;
            if (t) return 2;
            return 3;
        }

        // Nájdi behy: borderPt → (inside+) → borderPt
        for (int i = 0; i < n; i++)
        {
            var a = outer[i];
            var b = outer[(i + 1) % n];
            // Štart zafrezu: bod na hranici, ďalší ide dnu
            if (Inside(a) || !Inside(b)) continue;
            if (BorderId(a) < 0 && !IsOnAnyBorder(a, dx, dy)) continue;

            var path = new List<(double X, double Y)> { a, b };
            int j = (i + 1) % n;
            bool closedOk = false;
            for (int step = 0; step < n - 1; step++)
            {
                j = (j + 1) % n;
                var p = outer[j];
                path.Add(p);
                if (!Inside(p) && IsOnAnyBorder(p, dx, dy))
                {
                    closedOk = true;
                    break;
                }
            }
            if (!closedOk || path.Count < 3) continue;

            int idStart = BorderId(path[0]);
            if (idStart < 0) idStart = GuessBorderNear(path[0], dx, dy);
            int idEnd = BorderId(path[^1]);
            if (idEnd < 0) idEnd = GuessBorderNear(path[^1], dx, dy);

            // U: rovnaká strana
            if (idStart >= 0 && idStart == idEnd)
            {
                result.Add(DedupConsecutive(path));
                i = (j - 1 + n) % n; // preskoč spracovaný úsek
                continue;
            }

            // L: susedné strany — otvorené L (bez vonkajšieho rohu AABB; roh by z L urobil U)
            if (idStart >= 0 && idEnd >= 0 && AreAdjacentBorders(idStart, idEnd))
            {
                result.Add(DedupConsecutive(path));
                i = (j - 1 + n) % n;
            }
        }

        return result
            .GroupBy(p => string.Join("|", p.Select(pt => $"{pt.X:0.##},{pt.Y:0.##}")))
            .Select(g => g.First())
            .ToList();
    }

    private static bool IsOnAnyBorder((double X, double Y) p, double dx, double dy)
        => Near(p.X, 0) || Near(p.X, dx) || Near(p.Y, 0) || Near(p.Y, dy)
           || p.X < Eps || p.X > dx - Eps || p.Y < Eps || p.Y > dy - Eps;

    private static int GuessBorderNear((double X, double Y) p, double dx, double dy)
    {
        double db = Math.Abs(p.Y - 0);
        double dr = Math.Abs(p.X - dx);
        double dt = Math.Abs(p.Y - dy);
        double dl = Math.Abs(p.X - 0);
        double m = Math.Min(Math.Min(db, dr), Math.Min(dt, dl));
        if (Near(m, db)) return 0;
        if (Near(m, dr)) return 1;
        if (Near(m, dt)) return 2;
        return 3;
    }

    private static bool AreAdjacentBorders(int a, int b)
        => Math.Abs(a - b) == 1 || (a == 0 && b == 3) || (a == 3 && b == 0);

    private static bool IsAxisAlignedRectangleMatchingWorkpiece(
        DielecModel diel, List<(double X, double Y)> outer)
    {
        if (outer.Count != 4) return false;
        var (dx, dy, _) = PartRules.For(diel).WorkpieceSize(diel);
        double minX = outer.Min(p => p.X), maxX = outer.Max(p => p.X);
        double minY = outer.Min(p => p.Y), maxY = outer.Max(p => p.Y);
        if (Math.Abs(minX) > 1 || Math.Abs(minY) > 1) return false;
        if (Math.Abs(maxX - dx) > 1 || Math.Abs(maxY - dy) > 1) return false;
        // Všetky body na rohoch obdĺžnika
        foreach (var p in outer)
        {
            bool onCorner =
                (Near(p.X, minX) || Near(p.X, maxX)) &&
                (Near(p.Y, minY) || Near(p.Y, maxY));
            if (!onCorner) return false;
        }
        return true;
    }

    internal static void LocalAabbToWorkpieceXY(
        DielecModel diel, double lx, double ly, double lz, out double wpX, out double wpY)
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

        // Face X/Y = poradie AABB (ako Excel), potom swap ak X workpiece = väčší rozmer
        if (d[a0] >= d[a1])
        {
            wpX = c[a0];
            wpY = c[a1];
        }
        else
        {
            wpX = c[a1];
            wpY = c[a0];
        }
    }

    private static List<((double X, double Y) A, (double X, double Y) B)> BuildOrthoEdges(
        List<(double X, double Y)> pts)
    {
        var edges = new List<((double X, double Y) A, (double X, double Y) B)>();
        for (int i = 0; i < pts.Count; i++)
        {
            for (int j = i + 1; j < pts.Count; j++)
            {
                var a = pts[i];
                var b = pts[j];
                bool sameX = Near(a.X, b.X);
                bool sameY = Near(a.Y, b.Y);
                if (sameX == sameY) continue; // diagonála alebo totožné

                double lo = sameX ? Math.Min(a.Y, b.Y) : Math.Min(a.X, b.X);
                double hi = sameX ? Math.Max(a.Y, b.Y) : Math.Max(a.X, b.X);
                bool mid = false;
                foreach (var p in pts)
                {
                    if (sameX)
                    {
                        if (!Near(p.X, a.X)) continue;
                        if (p.Y > lo + Eps && p.Y < hi - Eps) { mid = true; break; }
                    }
                    else
                    {
                        if (!Near(p.Y, a.Y)) continue;
                        if (p.X > lo + Eps && p.X < hi - Eps) { mid = true; break; }
                    }
                }
                if (!mid) edges.Add((a, b));
            }
        }
        return edges;
    }

    /// <summary>
    /// Odstráni falošnú hranu cez ústie zálomu na obvode AABB
    /// (napr. bok: (237.5,0)–(321.5,0) keď existujú body (237.5,30),(321.5,30)).
    /// </summary>
    private static void RemoveBorderNotchChords(
        List<(double X, double Y)> pts,
        List<((double X, double Y) A, (double X, double Y) B)> edges)
    {
        if (pts.Count == 0) return;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);

        bool Has(double x, double y) => pts.Any(p => Near(p.X, x) && Near(p.Y, y));

        for (int i = edges.Count - 1; i >= 0; i--)
        {
            var (a, b) = edges[i];
            bool horiz = Near(a.Y, b.Y);
            bool vert = Near(a.X, b.X);
            if (horiz == vert) continue;

            if (horiz)
            {
                double y = a.Y;
                bool onBorder = Near(y, minY) || Near(y, maxY);
                if (!onBorder) continue;
                double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
                double span = x1 - x0;
                double full = maxX - minX;
                if (span < 1 || span > full * 0.85) continue; // nie cez celú stranu
                double[] tryY = Near(y, minY)
                    ? pts.Where(p => p.Y > y + Eps && Near(p.X, x0)).Select(p => p.Y).Distinct().OrderBy(v => v).Take(1).ToArray()
                    : pts.Where(p => p.Y < y - Eps && Near(p.X, x0)).Select(p => p.Y).Distinct().OrderByDescending(v => v).Take(1).ToArray();
                if (tryY.Length == 0) continue;
                double yin = tryY[0];
                double depth = Math.Abs(yin - y);
                if (depth < 1 || depth > Math.Min(120, (maxY - minY) * 0.35)) continue;
                if (Has(x0, yin) && Has(x1, yin))
                    edges.RemoveAt(i);
            }
            else
            {
                double x = a.X;
                bool onBorder = Near(x, minX) || Near(x, maxX);
                if (!onBorder) continue;
                double y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                double span = y1 - y0;
                double full = maxY - minY;
                if (span < 1 || span > full * 0.85) continue;
                double[] tryX = Near(x, minX)
                    ? pts.Where(p => p.X > x + Eps && Near(p.Y, y0)).Select(p => p.X).Distinct().OrderBy(v => v).Take(1).ToArray()
                    : pts.Where(p => p.X < x - Eps && Near(p.Y, y0)).Select(p => p.X).Distinct().OrderByDescending(v => v).Take(1).ToArray();
                if (tryX.Length == 0) continue;
                double xin = tryX[0];
                double depth = Math.Abs(xin - x);
                if (depth < 1 || depth > Math.Min(120, (maxX - minX) * 0.35)) continue;
                if (Has(xin, y0) && Has(xin, y1))
                    edges.RemoveAt(i);
            }
        }
    }

    private static List<List<(double X, double Y)>> FindCycles(
        List<((double X, double Y) A, (double X, double Y) B)> edges)
    {
        var adj = new Dictionary<(double X, double Y), List<(double X, double Y)>>(new PtComparer());
        void Add((double X, double Y) a, (double X, double Y) b)
        {
            if (!adj.ContainsKey(a)) adj[a] = new();
            if (!adj.ContainsKey(b)) adj[b] = new();
            if (!adj[a].Any(n => Near(n.X, b.X) && Near(n.Y, b.Y))) adj[a].Add(b);
            if (!adj[b].Any(n => Near(n.X, a.X) && Near(n.Y, a.Y))) adj[b].Add(a);
        }
        foreach (var (a, b) in edges) Add(a, b);

        var used = new HashSet<string>();
        var cycles = new List<List<(double X, double Y)>>();

        foreach (var start in adj.Keys.ToList())
        {
            foreach (var nb in adj[start].ToList())
            {
                string ek = EdgeKey(start, nb);
                if (!used.Add(ek)) continue;

                var cycle = new List<(double X, double Y)> { start };
                var prev = start;
                var cur = nb;
                bool closed = false;
                for (int g = 0; g < adj.Count + 3; g++)
                {
                    cycle.Add(cur);
                    if (Near(cur.X, start.X) && Near(cur.Y, start.Y) && cycle.Count > 3)
                    {
                        closed = true;
                        break;
                    }
                    var next = PickLeft(adj, prev, cur);
                    if (next == null) break;
                    string nek = EdgeKey(cur, next.Value);
                    if (!used.Add(nek) && !(Near(next.Value.X, start.X) && Near(next.Value.Y, start.Y)))
                        break;
                    prev = cur;
                    cur = next.Value;
                }
                if (!closed) continue;
                if (Near(cycle[^1].X, cycle[0].X) && Near(cycle[^1].Y, cycle[0].Y))
                    cycle.RemoveAt(cycle.Count - 1);
                if (cycle.Count >= 3) cycles.Add(cycle);
            }
        }
        return cycles;
    }

    private static (double X, double Y)? PickLeft(
        Dictionary<(double X, double Y), List<(double X, double Y)>> adj,
        (double X, double Y) prev,
        (double X, double Y) cur)
    {
        if (!adj.TryGetValue(cur, out var nbs))
        {
            var k = adj.Keys.FirstOrDefault(x => Near(x.X, cur.X) && Near(x.Y, cur.Y));
            if (!adj.TryGetValue(k, out nbs)) return null;
            cur = k;
        }
        var candidates = nbs.Where(n => !(Near(n.X, prev.X) && Near(n.Y, prev.Y))).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        var inn = (cur.X - prev.X, cur.Y - prev.Y);
        return candidates
            .OrderByDescending(n => inn.Item1 * (n.Y - cur.Y) - inn.Item2 * (n.X - cur.X))
            .First();
    }

    private static int IndexOfNear(List<(double X, double Y)> pts, double x, double y)
    {
        for (int i = 0; i < pts.Count; i++)
            if (Near(pts[i].X, x) && Near(pts[i].Y, y)) return i;
        return -1;
    }

    private static List<(double X, double Y)> DedupConsecutive(List<(double X, double Y)> path)
    {
        var r = new List<(double X, double Y)>();
        foreach (var p in path)
        {
            if (r.Count > 0 && Near(r[^1].X, p.X) && Near(r[^1].Y, p.Y)) continue;
            r.Add(p);
        }
        return r;
    }

    private static bool PointInPoly((double X, double Y) p, List<(double X, double Y)> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            bool intersect = ((a.Y > p.Y) != (b.Y > p.Y))
                && (p.X < (b.X - a.X) * (p.Y - a.Y) / ((b.Y - a.Y) + 1e-30) + a.X);
            if (intersect) inside = !inside;
        }
        return inside;
    }

    private static double Shoelace(List<(double X, double Y)> poly)
    {
        double a = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a * 0.5;
    }

    private static string CycleSignature(List<(double X, double Y)> c)
        => string.Join("|", c.Select(p => $"{p.X:0.##},{p.Y:0.##}").OrderBy(s => s));

    private static string EdgeKey((double X, double Y) a, (double X, double Y) b)
    {
        bool swap = a.X > b.X + Eps || (Near(a.X, b.X) && a.Y > b.Y);
        var x = swap ? b : a;
        var y = swap ? a : b;
        return $"{x.X:0.###}:{x.Y:0.###}|{y.X:0.###}:{y.Y:0.###}";
    }

    private static double Round2(double v) => Math.Round(v, 2);
    private static double Snap(double v) => Math.Round(v / Eps) * Eps;
    private static bool Near(double a, double b) => Math.Abs(a - b) <= Eps * 1.5;

    private sealed class PtComparer : IEqualityComparer<(double X, double Y)>
    {
        public bool Equals((double X, double Y) a, (double X, double Y) b)
            => Near(a.X, b.X) && Near(a.Y, b.Y);
        public int GetHashCode((double X, double Y) p)
            => HashCode.Combine(Math.Round(p.X / Eps), Math.Round(p.Y / Eps));
    }
}
