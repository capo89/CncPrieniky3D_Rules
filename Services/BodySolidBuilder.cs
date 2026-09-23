using System.Windows.Media;
using System.Windows.Media.Media3D;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Mesh dielca: preferuje exportované BREP plochy (Plochy dielca),
/// inak fallback z vrcholov (ortogonálne hrany).
/// </summary>
internal static class BodySolidBuilder
{
    private const double Eps = 0.08;

    public static MeshGeometry3D? TryBuild(DielecModel diel, Point3D origin)
    {
        // 1) Bounding obdĺžnik + Vyrezy dielca (zdroj pravdy)
        MeshGeometry3D? fromVyrez = AabbVyrezMeshBuilder.TryBuild(diel, origin);
        if (fromVyrez != null)
            return fromVyrez;

        // 2) Legacy BREP plochy
        MeshGeometry3D? fromPlochy = TryBuildFromPlochy(diel, origin);
        if (fromPlochy != null)
            return fromPlochy;

        if (diel.Body.Count < 4)
            return null;

        var pts = diel.Body
            .Select(b => (X: Snap(b.PosX), Y: Snap(b.PosY), Z: Snap(b.PosZ)))
            .Distinct(new Pt3Comparer())
            .ToList();

        if (pts.Count < 4)
            return null;

        var edges = BuildEdges(pts);
        if (edges.Count < 4)
            return null;

        var faces = ExtractFaces(pts, edges);
        if (faces.Count == 0)
            return null;

        return BuildMesh(faces, origin);
    }

    public static string Describe(DielecModel diel)
    {
        if (diel.RozmerX > 0.1 && diel.RozmerY > 0.1 && diel.RozmerZ > 0.1)
            return AabbVyrezMeshBuilder.Describe(diel);

        if (diel.Plochy.Count > 0)
        {
            int nPl = diel.Plochy.Select(p => p.PlochaCislo).Distinct().Count();
            int nDiera = diel.Plochy.Where(p => p.Typ.StartsWith("diera", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.PlochaCislo).Distinct().Count();
            return $"BREP plochy: {nPl} (diery-loop {nDiera}), bodov {diel.Plochy.Count}";
        }

        if (diel.Body.Count < 4) return "málo bodov";
        var pts = diel.Body
            .Select(b => (X: Snap(b.PosX), Y: Snap(b.PosY), Z: Snap(b.PosZ)))
            .Distinct(new Pt3Comparer())
            .ToList();
        var edges = BuildEdges(pts);
        var faces = ExtractFaces(pts, edges);
        int holes = faces.Sum(f => f.Holes.Count);
        return $"{pts.Count} bodov, {edges.Count} hrán, {faces.Count} plôch, {holes} dier (fallback)";
    }

    /// <summary>Triangulácia exportovaných BREP polygónov (obrys; diery zatiaľ vynechané vo fane).</summary>
    private static MeshGeometry3D? TryBuildFromPlochy(DielecModel diel, Point3D origin)
    {
        if (diel.Plochy.Count < 9)
            return null;

        var mesh = new MeshGeometry3D();
        var groups = diel.Plochy
            .GroupBy(p => p.PlochaCislo)
            .OrderBy(g => g.Key);

        int facesOk = 0;
        foreach (var g in groups)
        {
            // len obrys plochy (interior loops = diery v ploche — fan bez dier stačí na vizuál korpusu;
            // diera ako samostatná plocha steny valca má Typ obrys)
            var ring = g
                .Where(p => !p.Typ.StartsWith("diera", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.BodCislo)
                .Select(p => new Point3D(origin.X + p.PosX, origin.Y + p.PosY, origin.Z + p.PosZ))
                .ToList();

            if (ring.Count < 3)
                continue;

            // odstráň zatvárací duplicitný bod
            if (ring.Count > 1 && (ring[0] - ring[^1]).Length < 0.15)
                ring.RemoveAt(ring.Count - 1);

            if (ring.Count < 3)
                continue;

            if (AddFanTriangles(mesh, ring))
                facesOk++;
        }

        if (facesOk < 2 || mesh.TriangleIndices.Count < 3)
            return null;

        mesh.Freeze();
        return mesh;
    }

    private static bool AddFanTriangles(MeshGeometry3D mesh, List<Point3D> ring)
    {
        // nájdi nekolineárny základ
        int i1 = -1, i2 = -1;
        for (int i = 1; i < ring.Count && i1 < 0; i++)
        {
            if ((ring[i] - ring[0]).Length > 0.2)
                i1 = i;
        }
        if (i1 < 0) return false;

        Vector3D e1 = ring[i1] - ring[0];
        for (int i = 1; i < ring.Count; i++)
        {
            if (i == i1) continue;
            Vector3D e2 = ring[i] - ring[0];
            if (Vector3D.CrossProduct(e1, e2).Length > 0.05)
            {
                i2 = i;
                break;
            }
        }
        if (i2 < 0) return false;

        // Fan z bodu 0 — OK pre konvexné; pre konkávne môže prekrývať, ale lepšie ako AABB
        int baseIndex = mesh.Positions.Count;
        foreach (var p in ring)
            mesh.Positions.Add(p);

        for (int i = 1; i < ring.Count - 1; i++)
        {
            mesh.TriangleIndices.Add(baseIndex);
            mesh.TriangleIndices.Add(baseIndex + i);
            mesh.TriangleIndices.Add(baseIndex + i + 1);
        }

        return true;
    }

    private static List<((double X, double Y, double Z) A, (double X, double Y, double Z) B)> BuildEdges(
        List<(double X, double Y, double Z)> pts)
    {
        var edges = new List<((double X, double Y, double Z) A, (double X, double Y, double Z) B)>();
        for (int i = 0; i < pts.Count; i++)
        {
            for (int j = i + 1; j < pts.Count; j++)
            {
                var a = pts[i];
                var b = pts[j];
                int same = 0;
                int axis = -1;
                if (Near(a.X, b.X)) same++; else axis = 0;
                if (Near(a.Y, b.Y)) same++; else axis = 1;
                if (Near(a.Z, b.Z)) same++; else axis = 2;
                if (same != 2 || axis < 0) continue;

                double lo = axis == 0 ? Math.Min(a.X, b.X) : axis == 1 ? Math.Min(a.Y, b.Y) : Math.Min(a.Z, b.Z);
                double hi = axis == 0 ? Math.Max(a.X, b.X) : axis == 1 ? Math.Max(a.Y, b.Y) : Math.Max(a.Z, b.Z);

                bool mid = false;
                foreach (var p in pts)
                {
                    bool onLine = axis switch
                    {
                        0 => Near(p.Y, a.Y) && Near(p.Z, a.Z),
                        1 => Near(p.X, a.X) && Near(p.Z, a.Z),
                        _ => Near(p.X, a.X) && Near(p.Y, a.Y)
                    };
                    if (!onLine) continue;
                    double t = axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
                    if (t > lo + Eps && t < hi - Eps)
                    {
                        mid = true;
                        break;
                    }
                }
                if (!mid)
                    edges.Add((a, b));
            }
        }
        return edges;
    }

    private static List<PlanarFace> ExtractFaces(
        List<(double X, double Y, double Z)> pts,
        List<((double X, double Y, double Z) A, (double X, double Y, double Z) B)> edges)
    {
        var faces = new List<PlanarFace>();

        foreach (int axis in new[] { 0, 1, 2 })
        {
            var planeValues = pts
                .Select(p => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z)
                .Select(Snap)
                .Distinct()
                .ToList();

            foreach (double plane in planeValues)
            {
                var planeEdges = new List<((double U, double V) A, (double U, double V) B)>();

                foreach (var (a, b) in edges)
                {
                    double aP = axis == 0 ? a.X : axis == 1 ? a.Y : a.Z;
                    double bP = axis == 0 ? b.X : axis == 1 ? b.Y : b.Z;
                    if (!Near(aP, plane) || !Near(bP, plane))
                        continue;
                    planeEdges.Add((ToUv(a, axis), ToUv(b, axis)));
                }

                if (planeEdges.Count < 3)
                    continue;

                var cycles = FindCycles2D(planeEdges)
                    .Where(c => c.Count >= 3 && Math.Abs(Shoelace(c)) > 1.0)
                    .Select(NormalizeCycle)
                    .GroupBy(CycleSignature)
                    .Select(g => g.First())
                    .OrderByDescending(c => Math.Abs(Shoelace(c)))
                    .ToList();

                if (cycles.Count == 0)
                    continue;

                // Najväčší cyklus = vonkajšok; menšie vnútri = diery
                var outer = cycles[0];
                if (Shoelace(outer) < 0) outer.Reverse();

                var holes = new List<List<(double U, double V)>>();
                for (int i = 1; i < cycles.Count; i++)
                {
                    var c = cycles[i];
                    var mid = CycleCentroid(c);
                    if (!PointInPoly(mid, outer))
                        continue;
                    if (Shoelace(c) > 0) c.Reverse(); // diera = CW
                    holes.Add(c);
                }

                faces.Add(new PlanarFace(axis, plane, outer, holes));
            }
        }

        return faces;
    }

    private sealed class PlanarFace
    {
        public int Axis { get; }
        public double Plane { get; }
        public List<(double U, double V)> Outer { get; }
        public List<List<(double U, double V)>> Holes { get; }

        public PlanarFace(
            int axis,
            double plane,
            List<(double U, double V)> outer,
            List<List<(double U, double V)>> holes)
        {
            Axis = axis;
            Plane = plane;
            Outer = outer;
            Holes = holes;
        }
    }

    private static (double U, double V) ToUv((double X, double Y, double Z) p, int axis)
        => axis switch
        {
            0 => (p.Y, p.Z),
            1 => (p.X, p.Z),
            _ => (p.X, p.Y)
        };

    private static (double X, double Y, double Z) FromUv(double u, double v, int axis, double plane)
        => axis switch
        {
            0 => (plane, u, v),
            1 => (u, plane, v),
            _ => (u, v, plane)
        };

    private static List<List<(double U, double V)>> FindCycles2D(
        List<((double U, double V) A, (double U, double V) B)> edges)
    {
        var adj = new Dictionary<(double U, double V), List<(double U, double V)>>(new Pt2Comparer());

        void Add((double U, double V) a, (double U, double V) b)
        {
            if (!adj.ContainsKey(a)) adj[a] = new List<(double U, double V)>();
            if (!adj.ContainsKey(b)) adj[b] = new List<(double U, double V)>();
            if (!adj[a].Any(n => Near(n.U, b.U) && Near(n.V, b.V))) adj[a].Add(b);
            if (!adj[b].Any(n => Near(n.U, a.U) && Near(n.V, a.V))) adj[b].Add(a);
        }

        foreach (var (a, b) in edges)
            Add(a, b);

        var used = new HashSet<string>();
        var cycles = new List<List<(double U, double V)>>();

        foreach (var start in adj.Keys.ToList())
        {
            foreach (var nb in adj[start].ToList())
            {
                string ek = EdgeKey2(start, nb);
                if (!used.Add(ek)) continue;

                var cycle = new List<(double U, double V)> { start };
                var prev = start;
                var cur = nb;
                bool closed = false;

                for (int g = 0; g < adj.Count + 3; g++)
                {
                    cycle.Add(cur);
                    if (Near(cur.U, start.U) && Near(cur.V, start.V) && cycle.Count > 3)
                    {
                        closed = true;
                        break;
                    }

                    var next = PickLeft(adj, prev, cur);
                    if (next == null) break;

                    string nek = EdgeKey2(cur, next.Value);
                    if (!used.Add(nek) && !(Near(next.Value.U, start.U) && Near(next.Value.V, start.V)))
                        break;

                    prev = cur;
                    cur = next.Value;
                }

                if (!closed) continue;
                if (Near(cycle[^1].U, cycle[0].U) && Near(cycle[^1].V, cycle[0].V))
                    cycle.RemoveAt(cycle.Count - 1);
                if (cycle.Count >= 3)
                    cycles.Add(cycle);
            }
        }

        return cycles;
    }

    private static (double U, double V)? PickLeft(
        Dictionary<(double U, double V), List<(double U, double V)>> adj,
        (double U, double V) prev,
        (double U, double V) cur)
    {
        if (!adj.TryGetValue(cur, out var nbs))
        {
            var k = adj.Keys.FirstOrDefault(x => Near(x.U, cur.U) && Near(x.V, cur.V));
            if (!adj.TryGetValue(k, out nbs)) return null;
            cur = k;
        }

        var candidates = nbs
            .Where(n => !(Near(n.U, prev.U) && Near(n.V, prev.V)))
            .ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var inn = (cur.U - prev.U, cur.V - prev.V);
        return candidates
            .OrderByDescending(n => Cross(inn, (n.U - cur.U, n.V - cur.V)))
            .First();
    }

    private static MeshGeometry3D? BuildMesh(List<PlanarFace> faces, Point3D origin)
    {
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        var indexOf = new Dictionary<(double X, double Y, double Z), int>(new Pt3Comparer());

        int AddPt((double X, double Y, double Z) p)
        {
            if (indexOf.TryGetValue(p, out int existing))
                return existing;
            int i = positions.Count;
            positions.Add(new Point3D(origin.X + p.X, origin.Y + p.Y, origin.Z + p.Z));
            indexOf[p] = i;
            return i;
        }

        void AddTri((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
        {
            indices.Add(AddPt(a));
            indices.Add(AddPt(b));
            indices.Add(AddPt(c));
        }

        foreach (var face in faces)
        {
            // Ortogonálna mriežka: bunky vo vnútri outer a mimo dier = materiál
            var us = face.Outer.Select(p => p.U)
                .Concat(face.Holes.SelectMany(h => h.Select(p => p.U)))
                .Select(Snap)
                .Distinct()
                .OrderBy(v => v)
                .ToList();
            var vs = face.Outer.Select(p => p.V)
                .Concat(face.Holes.SelectMany(h => h.Select(p => p.V)))
                .Select(Snap)
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            if (us.Count < 2 || vs.Count < 2)
                continue;

            for (int i = 0; i < us.Count - 1; i++)
            {
                for (int j = 0; j < vs.Count - 1; j++)
                {
                    double u0 = us[i], u1 = us[i + 1];
                    double v0 = vs[j], v1 = vs[j + 1];
                    if (u1 - u0 < Eps || v1 - v0 < Eps)
                        continue;

                    double cu = (u0 + u1) * 0.5;
                    double cv = (v0 + v1) * 0.5;
                    if (!PointInPoly((cu, cv), face.Outer))
                        continue;
                    if (face.Holes.Any(h => PointInPoly((cu, cv), h)))
                        continue;

                    var p00 = FromUv(u0, v0, face.Axis, face.Plane);
                    var p10 = FromUv(u1, v0, face.Axis, face.Plane);
                    var p11 = FromUv(u1, v1, face.Axis, face.Plane);
                    var p01 = FromUv(u0, v1, face.Axis, face.Plane);

                    // Orientácia podľa osi plochy
                    if (face.Axis == 1) // Y=const – otoč pre konzistentné normály
                    {
                        AddTri(p00, p01, p11);
                        AddTri(p00, p11, p10);
                    }
                    else
                    {
                        AddTri(p00, p10, p11);
                        AddTri(p00, p11, p01);
                    }
                }
            }
        }

        if (indices.Count < 3)
            return null;

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = indices
        };
        mesh.Freeze();
        return mesh;
    }

    private static List<(double U, double V)> NormalizeCycle(List<(double U, double V)> c)
        => c.Select(p => (Snap(p.U), Snap(p.V))).ToList();

    private static string CycleSignature(List<(double U, double V)> c)
        => string.Join("|", c.Select(p => $"{p.U:0.##},{p.V:0.##}").OrderBy(s => s));

    private static (double U, double V) CycleCentroid(List<(double U, double V)> c)
        => (c.Average(p => p.U), c.Average(p => p.V));

    private static bool PointInPoly((double U, double V) p, List<(double U, double V)> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            bool intersect = ((a.V > p.V) != (b.V > p.V))
                && (p.U < (b.U - a.U) * (p.V - a.V) / ((b.V - a.V) + 1e-30) + a.U);
            if (intersect) inside = !inside;
        }
        return inside;
    }

    private static double Shoelace(List<(double U, double V)> poly)
    {
        double a = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            a += p.U * q.V - q.U * p.V;
        }
        return a * 0.5;
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b)
        => a.X * b.Y - a.Y * b.X;

    private static string EdgeKey2((double U, double V) a, (double U, double V) b)
    {
        bool swap = a.U > b.U + Eps || (Near(a.U, b.U) && a.V > b.V);
        var x = swap ? b : a;
        var y = swap ? a : b;
        return $"{x.U:0.###}:{x.V:0.###}|{y.U:0.###}:{y.V:0.###}";
    }

    private static double Snap(double v) => Math.Round(v / Eps) * Eps;
    private static bool Near(double a, double b) => Math.Abs(a - b) <= Eps * 1.5;

    private sealed class Pt3Comparer : IEqualityComparer<(double X, double Y, double Z)>
    {
        public bool Equals((double X, double Y, double Z) a, (double X, double Y, double Z) b)
            => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);

        public int GetHashCode((double X, double Y, double Z) p)
            => HashCode.Combine(Math.Round(p.X / Eps), Math.Round(p.Y / Eps), Math.Round(p.Z / Eps));
    }

    private sealed class Pt2Comparer : IEqualityComparer<(double U, double V)>
    {
        public bool Equals((double U, double V) a, (double U, double V) b)
            => Near(a.U, b.U) && Near(a.V, b.V);

        public int GetHashCode((double U, double V) p)
            => HashCode.Combine(Math.Round(p.U / Eps), Math.Round(p.V / Eps));
    }
}
