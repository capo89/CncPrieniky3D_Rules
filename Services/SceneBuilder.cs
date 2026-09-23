using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CncPrieniky3D.Models;
using HelixToolkit.Wpf;

namespace CncPrieniky3D.Services;

internal static class SceneBuilder
{
    private static ViewColors Colors => ViewColors.Current;

    public enum ViewMode { Assembly, SinglePart }

    public enum SceneGroup { Korpus, Sufliky, CelaSkrinka }

    public static IEnumerable<Visual3D> BuildVisuals(
        ExportDocument doc,
        DielecModel? selected,
        IReadOnlyCollection<ContactMark> selectedContacts,
        ViewMode mode,
        SceneGroup group,
        bool showPanels,
        bool showBody,
        bool showCnc,
        bool showContacts,
        bool showContactLabels = true,
        double panelOpacityOverride = -1)
    {
        var list = new List<Visual3D>();
        var selectedSet = selectedContacts as HashSet<ContactMark>
            ?? selectedContacts.ToHashSet();

        bool previewSkrinka = group == SceneGroup.CelaSkrinka;
        // Celá skrinka: bez plôch dotykov; kolíky/vŕtania sa kreslia osobitne.
        if (previewSkrinka)
            showContacts = false;

        IEnumerable<DielecModel> pool = group switch
        {
            SceneGroup.Sufliky => doc.SuflikDiely,
            SceneGroup.CelaSkrinka => doc.Diely,
            _ => doc.KorpusDiely
        };

        IEnumerable<DielecModel> parts;
        if (mode == ViewMode.SinglePart && selected != null)
        {
            parts = new[] { selected };
        }
        else if (group == SceneGroup.Sufliky)
        {
            // Nikdy nekresli AABB „pozicia“ (490×510×277), keď existujú reálne dielce —
            // pozícia je plný box a kolíky v ňom prakticky zaniknú.
            var individuals = pool.Where(d => !d.JeSuflikPozicia && !d.JeSkryty).ToList();
            parts = individuals.Count > 0
                ? individuals
                : pool.Where(d => d.JeSuflikPozicia && !d.JeSkryty);
        }
        else if (group == SceneGroup.CelaSkrinka)
        {
            // Korpus + reálne dielce šuflíkov (bez AABB „pozicia“ boxov).
            var suflik = doc.SuflikDiely.Where(d => !d.JeSuflikPozicia && !d.JeSkryty).ToList();
            if (suflik.Count == 0)
                suflik = doc.SuflikDiely.Where(d => d.JeSuflikPozicia && !d.JeSkryty).ToList();
            parts = doc.KorpusDiely.Where(d => !d.JeSkryty).Concat(suflik);
        }
        else
        {
            parts = pool.Where(d => !d.JeSkryty);
        }

        var partsList = parts.ToList();

        // WCS z AutoCADu môže byť ~1e5–1e6 mm — float depth buffer potom „zožerie“
        // tenké kolíky/rúry. Scénu posunieme k (0,0,0).
        Vector3D sceneShift = mode == ViewMode.Assembly && partsList.Count > 0
            ? new Vector3D(
                partsList.Min(d => d.WcsMinX),
                partsList.Min(d => d.WcsMinY),
                partsList.Min(d => d.WcsMinZ))
            : new Vector3D(0, 0, 0);

        // Šuflíky: najprv kolíky (nepriehľadné), potom priesvitné panely —
        // inak Z-buffer panely zakryjú kolíky ešte pred alpha blendom.
        bool kolikyCezMaterial = group is SceneGroup.Sufliky or SceneGroup.CelaSkrinka;
        var deferredPanels = kolikyCezMaterial ? new List<Visual3D>() : null;

        foreach (var diel in partsList)
        {
            if (diel.JeSkryty && mode != ViewMode.SinglePart)
                continue;
            bool isSel = selected != null && ReferenceEquals(selected, diel);
            Point3D origin = mode == ViewMode.Assembly
                ? ShiftPoint(new Point3D(diel.WcsMinX, diel.WcsMinY, diel.WcsMinZ), sceneShift)
                : new Point3D(0, 0, 0);

            if (showPanels)
            {
                double panelOp = panelOpacityOverride >= 0
                    ? Math.Clamp(panelOpacityOverride, 0.05, 0.95)
                    : group switch
                    {
                        SceneGroup.Sufliky => 0.28,
                        SceneGroup.CelaSkrinka => 0.38,
                        _ => 0.92
                    };
                var panelBucket = deferredPanels ?? list;
                var solid = CreatePanelFromBody(diel, origin, isSel, panelOp);
                if (solid != null)
                {
                    panelBucket.Add(solid);
                }
                else
                {
                    panelBucket.Add(CreateAabbPanel(diel, origin, isSel, Math.Min(panelOp, 0.35)));
                    panelBucket.Add(CreatePanelEdges(diel, origin));
                }
            }

            if (showBody)
            {
                foreach (var b in diel.Body)
                    list.Add(CreateVertex(origin, b));
            }

            if (showCnc && !previewSkrinka)
            {
                AddCncMarkVisuals(list, diel, origin, overrideColor: null);
            }

            if (previewSkrinka && mode == ViewMode.Assembly)
            {
                AddCncMarkVisuals(list, diel, origin, Colors.CncZnaceniePreview);
                foreach (var diera in diel.Diery)
                {
                    foreach (var v in CreatePrienikDrillVisuals(diel, origin, diera, Colors.Diery))
                        list.Add(v);
                }
                foreach (var vyrez in AabbVyrezMeshBuilder.PreferKruhOverRectDiery(
                             diel.Vyrezy.Where(v => v.JeDiera)))
                {
                    foreach (var v in CreateVyrezDieraVisuals(diel, origin, vyrez, Colors.Diery))
                        list.Add(v);
                }

                if (diel.MaAbs && !diel.JeSuflikPozicia)
                {
                    foreach (var v in CreateAbsEdgeVisuals(doc, diel, origin, sceneShift))
                        list.Add(v);
                }
            }
        }

        if (showContacts && mode == ViewMode.Assembly && group == SceneGroup.Korpus)
        {
            foreach (var c in doc.Dotyky)
            {
                if (c.JeSuflikAuto || PartRules.IsPolicaContact(c))
                    continue;

                DielecModel? partA = doc.Diely.FirstOrDefault(d =>
                    string.Equals(d.Nazov, c.PartA, StringComparison.OrdinalIgnoreCase));
                DielecModel? partB = doc.Diely.FirstOrDefault(d =>
                    string.Equals(d.Nazov, c.PartB, StringComparison.OrdinalIgnoreCase));

                if (partA?.JeSkryty == true || partB?.JeSkryty == true)
                    continue;
                if (partA?.JeSuflik == true || partB?.JeSuflik == true)
                    continue;

                foreach (var v in CreateContactVisuals(
                             doc, c, selectedSet, partA, partB, sceneShift, showContactLabels))
                    list.Add(v);
            }
        }

        // Celá skrinka / Šuflíky: kolíky (pred priesvitnými panelmi, ak deferred).
        if (previewSkrinka || group == SceneGroup.Sufliky)
        {
            foreach (var c in doc.Dotyky)
            {
                if (!c.MaKoliky && !c.MaSkrutky) continue;
                if (group == SceneGroup.Sufliky && !c.JeSuflikAuto) continue;

                DielecModel? partA = doc.Diely.FirstOrDefault(d =>
                    string.Equals(d.Nazov, c.PartA, StringComparison.OrdinalIgnoreCase));
                DielecModel? partB = doc.Diely.FirstOrDefault(d =>
                    string.Equals(d.Nazov, c.PartB, StringComparison.OrdinalIgnoreCase));

                if (partA?.JeSkryty == true || partB?.JeSkryty == true)
                    continue;

                if (mode == ViewMode.SinglePart && selected != null)
                {
                    bool touches = string.Equals(c.PartA, selected.Nazov, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(c.PartB, selected.Nazov, StringComparison.OrdinalIgnoreCase);
                    if (!touches) continue;
                }

                foreach (var v in CreateHardwareOnlyVisuals(doc, c, partA, partB, sceneShift))
                    list.Add(v);
            }
        }

        if (deferredPanels != null)
            list.AddRange(deferredPanels);

        return list;
    }

    private static Point3D ShiftPoint(Point3D p, Vector3D shift)
        => new(p.X - shift.X, p.Y - shift.Y, p.Z - shift.Z);

    private static Brush OpaqueBrush(Color color)
        => new SolidColorBrush(color); // Opacity = 1

    private static IEnumerable<Visual3D> CreateHardwareOnlyVisuals(
        ExportDocument doc,
        ContactMark c,
        DielecModel? partA,
        DielecModel? partB,
        Vector3D sceneShift)
    {
        if (c.MaKoliky)
        {
            // Ø8 mm ako skrinka; mierne predĺžené + emissive — cez priesvitný materiál.
            double dia = 8.0;
            var kolikColor = Colors.Koliky;
            foreach (var p in ComputeKolikCenters(doc, c, partA, partB))
            {
                var a = ShiftPoint(p.P1, sceneShift);
                var b = ShiftPoint(p.P2, sceneShift);
                var dir = b - a;
                if (dir.Length > 0.2)
                {
                    dir.Normalize();
                    a -= dir * 4;
                    b += dir * 4;
                }
                yield return CreateEmissiveCylinder(a, b, dia * 0.5, kolikColor);
            }
        }

        if (c.MaSkrutky)
        {
            var fill = OpaqueBrush(Colors.Skrutky);
            foreach (var p in ComputeSkrutkyVisuals(doc, c, partA, partB))
            {
                yield return new PipeVisual3D
                {
                    Point1 = ShiftPoint(p.P1, sceneShift),
                    Point2 = ShiftPoint(p.P2, sceneShift),
                    Diameter = Math.Max(SkrutkaPriemerMm * 3, 8.0),
                    Fill = fill
                };
            }
        }
    }

    /// <summary>Valček kolíka — emissive, vždy viditeľný (PipeVisual3D Diffuse často zanikne).</summary>
    private static ModelVisual3D CreateEmissiveCylinder(Point3D p1, Point3D p2, double radius, Color color)
    {
        var axis = p2 - p1;
        double len = axis.Length;
        if (len < 0.2)
        {
            return new ModelVisual3D();
        }

        axis.Normalize();
        var tmp = Math.Abs(Vector3D.DotProduct(axis, new Vector3D(0, 0, 1))) < 0.9
            ? new Vector3D(0, 0, 1)
            : new Vector3D(1, 0, 0);
        var u = Vector3D.CrossProduct(axis, tmp);
        if (u.LengthSquared < 1e-12)
            u = Vector3D.CrossProduct(axis, new Vector3D(0, 1, 0));
        if (u.LengthSquared < 1e-12)
            return new ModelVisual3D();
        u.Normalize();
        var v = Vector3D.CrossProduct(axis, u);
        v.Normalize();

        const int sides = 12;
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        for (int i = 0; i < sides; i++)
        {
            double a = i * (2.0 * Math.PI / sides);
            var offset = u * (radius * Math.Cos(a)) + v * (radius * Math.Sin(a));
            positions.Add(p1 + offset);
            positions.Add(p2 + offset);
        }

        for (int i = 0; i < sides; i++)
        {
            int i0 = i * 2;
            int i1 = i0 + 1;
            int j0 = ((i + 1) % sides) * 2;
            int j1 = j0 + 1;
            indices.Add(i0); indices.Add(j0); indices.Add(i1);
            indices.Add(i1); indices.Add(j0); indices.Add(j1);
            indices.Add(i0); indices.Add(i1); indices.Add(j0);
            indices.Add(i1); indices.Add(j1); indices.Add(j0);
        }

        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze) brush.Freeze();
        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(brush));
        mat.Children.Add(new EmissiveMaterial(brush));

        return new ModelVisual3D
        {
            Content = new GeometryModel3D
            {
                Geometry = new MeshGeometry3D
                {
                    Positions = positions,
                    TriangleIndices = indices
                },
                Material = mat,
                BackMaterial = mat
            }
        };
    }

    private static IEnumerable<Visual3D> CreateContactVisuals(
        ExportDocument doc,
        ContactMark c,
        HashSet<ContactMark> selected,
        DielecModel? partA,
        DielecModel? partB,
        Vector3D sceneShift,
        bool showLabels)
    {
        bool isSel = selected.Contains(c);
        var color = isSel
            ? Colors.DotykVybrany
            : c.Oznaceny
                ? Colors.DotykOznaceny
                : Colors.Dotyky;

        var center = ShiftPoint(c.Center, sceneShift);
        yield return new ContactBoxVisual3D
        {
            Contact = c,
            Center = center,
            Length = Math.Max(c.Size.X, 0.5),
            Width = Math.Max(c.Size.Y, 0.5),
            Height = Math.Max(c.Size.Z, 0.5),
            Fill = new SolidColorBrush(color) { Opacity = isSel ? 0.95 : 0.8 }
        };

        if (showLabels)
        {
            double lift = 8 + Math.Max(c.Size.X, Math.Max(c.Size.Y, c.Size.Z)) * 0.02;
            var labelPos = new Point3D(center.X, center.Y, center.Z + lift);
            string label = c.LabelText;
            if (c.MaKoliky || c.MaSkrutky)
            {
                var parts = new List<string>();
                if (c.MaKoliky)
                    parts.Add($"{c.KolikSerie.Count}s/{c.CelkovyPocetKolikov}×k");
                if (c.MaSkrutky)
                    parts.Add($"{c.SkrutkySerie.Count}s/{c.CelkovyPocetSkrutiek}×s");
                label = $"{c.LabelText} ({string.Join(" ", parts)})";
            }
            yield return new BillboardTextVisual3D
            {
                Text = label,
                Position = labelPos,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)),
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                DepthOffset = 0.001
            };
        }

        if (c.MaKoliky)
        {
            foreach (var p in ComputeKolikCenters(doc, c, partA, partB))
            {
                yield return new PipeVisual3D
                {
                    Point1 = ShiftPoint(p.P1, sceneShift),
                    Point2 = ShiftPoint(p.P2, sceneShift),
                    Diameter = 8,
                    Fill = new SolidColorBrush(Colors.Koliky)
                };
            }
        }

        if (c.MaSkrutky)
        {
            foreach (var p in ComputeSkrutkyVisuals(doc, c, partA, partB))
            {
                yield return new PipeVisual3D
                {
                    Point1 = ShiftPoint(p.P1, sceneShift),
                    Point2 = ShiftPoint(p.P2, sceneShift),
                    Diameter = SkrutkaPriemerMm,
                    Fill = new SolidColorBrush(Colors.Skrutky)
                };
            }
        }
    }

    private const double KolikDoHranyMm = 23.0;
    private const double KolikDoPlochyMm = 13.0;
    private const double SkrutkaPriemerMm = 3.0;
    private const double SkrutkaZobrazenieDlzkaMm = 50.0;

    /// <summary>Stredy kolíkov na styčnej ploche (WCS) — pre Generovať.</summary>
    internal static IEnumerable<Point3D> GetKolikContactPoints(
        ExportDocument doc, ContactMark c, DielecModel? partA, DielecModel? partB)
        => ComputeKolikCenters(doc, c, partA, partB).Select(p => p.Contact);

    /// <summary>Stredy skrutiek na styčnej ploche (WCS) — pre Generovať.</summary>
    internal static IEnumerable<Point3D> GetSkrutkaContactPoints(ExportDocument doc, ContactMark c)
        => ComputeSkrutkaContactPoints(doc, c);

    /// <summary>
    /// Pozícia pozdĺž hlavnej osi: „Od predu“ od predku skrinky (nie od WCS min),
    /// ak vieme určiť prednú/zadnú stranu a primary = hĺbka.
    /// </summary>
    private static double AlongPrimary(
        ContactMark c,
        ExportDocument? doc,
        double odPredu,
        double roztec,
        int index,
        int pocet,
        bool zDruhejStrany,
        bool zoStredu,
        double primMin,
        double primMax,
        int primary)
    {
        if (zoStredu)
            return (primMin + primMax) * 0.5 - (pocet - 1) * 0.5 * roztec + index * roztec;

        double back = 0, front = 0;
        bool useCabinetFront = doc != null
            && SuflikCeloNormalizer.TryGetFrontBack(doc, out int fbAxis, out back, out front)
            && fbAxis == primary
            && Math.Abs(front - back) > 1.0;

        bool frontIsHigh = useCabinetFront && front > back;
        bool fromFront = !zDruhejStrany;
        // Bez informácie o predku: legacy — od predu = od primMin, z druhej = od primMax.
        bool startAtMax = useCabinetFront ? (fromFront == frontIsHigh) : zDruhejStrany;

        return startAtMax
            ? primMax - odPredu - index * roztec
            : primMin + odPredu + index * roztec;
    }

    /// <summary>
    /// Všetky série kolíkov. „Z druhej strany“ = Od predu od opačnej hrany.
    /// </summary>
    private static IEnumerable<(Point3D Contact, Point3D P1, Point3D P2)> ComputeKolikCenters(
        ExportDocument? doc,
        ContactMark c,
        DielecModel? partA,
        DielecModel? partB)
    {
        int n = c.Axis;
        int u = n == 0 ? 1 : 0;
        int v = n == 2 ? 1 : 2;
        if (u == v) v = n == 1 ? 2 : 1;

        double[] size = { c.Size.X, c.Size.Y, c.Size.Z };
        double[] center = { c.Center.X, c.Center.Y, c.Center.Z };

        int primary = size[u] >= size[v] ? u : v;
        int secondary = primary == u ? v : u;

        double primLen = size[primary];
        double primMin = center[primary] - primLen * 0.5;
        double primMax = center[primary] + primLen * 0.5;
        double secMid = center[secondary];

        ResolveKolikDepths(c, partA, partB, out double depthNeg, out double depthPos, out Vector3D axisPos);

        foreach (var serie in c.KolikSerie)
        {
            for (int i = 0; i < serie.PocetKolikov; i++)
            {
                double along = AlongPrimary(
                    c, doc, serie.OdPredu, serie.RoztecKolikov, i, serie.PocetKolikov,
                    serie.ZDruhejStrany, serie.ZoStredu, primMin, primMax, primary);

                if (along < primMin - 1 || along > primMax + 1)
                    continue;

                double[] p = { center[0], center[1], center[2] };
                p[primary] = along;
                p[secondary] = secMid;
                p[n] = center[n];

                var c0 = new Point3D(p[0], p[1], p[2]);
                yield return (c0, c0 - axisPos * depthNeg, c0 + axisPos * depthPos);
            }
        }
    }

    /// <summary>
    /// Skrutky vlastnou sériou (stred plochy ako kolíky).
    /// Zobrazenie: z protilahlej plochy, Ø3 × 50 mm.
    /// </summary>
    private static IEnumerable<(Point3D P1, Point3D P2)> ComputeSkrutkyVisuals(
        ExportDocument doc,
        ContactMark c,
        DielecModel? partA,
        DielecModel? partB)
    {
        ResolvePlochaSide(c, partA, partB, out Vector3D towardPlocha, out double thickness);

        foreach (var contactPt in ComputeSkrutkaContactPoints(doc, c))
        {
            var outer = contactPt + towardPlocha * thickness;
            yield return (outer, outer - towardPlocha * SkrutkaZobrazenieDlzkaMm);
        }
    }

    /// <summary>Pozície na styčnej ploche – stred šírky, pozdĺž dĺžky podľa série.</summary>
    private static IEnumerable<Point3D> ComputeSkrutkaContactPoints(ExportDocument? doc, ContactMark c)
    {
        int n = c.Axis;
        int u = n == 0 ? 1 : 0;
        int v = n == 2 ? 1 : 2;
        if (u == v) v = n == 1 ? 2 : 1;

        double[] size = { c.Size.X, c.Size.Y, c.Size.Z };
        double[] center = { c.Center.X, c.Center.Y, c.Center.Z };

        int primary = size[u] >= size[v] ? u : v;
        int secondary = primary == u ? v : u;

        double primLen = size[primary];
        double primMin = center[primary] - primLen * 0.5;
        double primMax = center[primary] + primLen * 0.5;
        double secMid = center[secondary];

        foreach (var serie in c.SkrutkySerie)
        {
            double roztec = serie.RoztecSkrutiek;
            if (serie.Symetricke)
            {
                if (!SkrutkaLayout.TryResolveSymetricParams(c, serie, out double len, out double odMin, out _))
                    continue;
                if (!SkrutkaLayout.TryComputeSymetricRoztec(
                        len,
                        serie.SymetriaMedziKolikmi ? serie.OdPredu : odMin,
                        serie.PocetSkrutiek, out roztec, out _))
                    continue;

                for (int i = 0; i < serie.PocetSkrutiek; i++)
                {
                    double along;
                    if (serie.PocetSkrutiek == 1)
                    {
                        double firstFromMin = serie.SymetriaMedziKolikmi ? odMin - serie.OdPredu : 0;
                        along = serie.SymetriaMedziKolikmi
                            ? primMin + firstFromMin + len * 0.5
                            : primMin + len * 0.5;
                    }
                    else
                    {
                        along = primMin + odMin + i * roztec;
                    }

                    if (along < primMin - 1 || along > primMax + 1)
                        continue;

                    double[] p = { center[0], center[1], center[2] };
                    p[primary] = along;
                    p[secondary] = secMid;
                    p[n] = center[n];
                    yield return new Point3D(p[0], p[1], p[2]);
                }
                continue;
            }

            for (int i = 0; i < serie.PocetSkrutiek; i++)
            {
                double along = AlongPrimary(
                    c, doc, serie.OdPredu, roztec, i, serie.PocetSkrutiek,
                    serie.ZDruhejStrany, zoStredu: false,
                    primMin, primMax, primary);

                if (along < primMin - 1 || along > primMax + 1)
                    continue;

                double[] p = { center[0], center[1], center[2] };
                p[primary] = along;
                p[secondary] = secMid;
                p[n] = center[n];
                yield return new Point3D(p[0], p[1], p[2]);
            }
        }
    }

    /// <summary>
    /// towardPlocha = smer od styku do plochy; thickness = hrúbka plochy pozdĺž osi kontaktu.
    /// </summary>
    private static void ResolvePlochaSide(
        ContactMark c,
        DielecModel? partA,
        DielecModel? partB,
        out Vector3D towardPlocha,
        out double thickness)
    {
        ResolveKolikDepths(c, partA, partB, out _, out _, out Vector3D axisPos);

        bool aPlocha = IsPlocha(partA, c.Axis);
        bool bPlocha = IsPlocha(partB, c.Axis);

        // Predvolene plocha = B (rovnako ako pri kolíkoch)
        bool plochaJeB = aPlocha == bPlocha ? true : bPlocha;
        DielecModel? plocha = plochaJeB ? partB : partA;
        towardPlocha = plochaJeB ? axisPos : -axisPos;
        thickness = ThicknessAlong(plocha, c.Axis);
        if (thickness < 1)
            thickness = KolikDoPlochyMm;
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

    /// <summary>
    /// axisPos smeruje k dielu B; depthNeg = hĺbka do A, depthPos = hĺbka do B.
    /// Plocha = kontakt v smere najtenšieho rozmeru dielu; inak hrana.
    /// </summary>
    private static void ResolveKolikDepths(
        ContactMark c,
        DielecModel? partA,
        DielecModel? partB,
        out double depthNeg,
        out double depthPos,
        out Vector3D axisPos)
    {
        axisPos = c.Axis switch
        {
            0 => new Vector3D(1, 0, 0),
            1 => new Vector3D(0, 1, 0),
            _ => new Vector3D(0, 0, 1)
        };

        double aMid = MidAlong(partA, c.Axis);
        double bMid = MidAlong(partB, c.Axis);
        // +axisPos má ísť smerom k B
        if (bMid < aMid)
            axisPos = -axisPos;

        bool aPlocha = IsPlocha(partA, c.Axis);
        bool bPlocha = IsPlocha(partB, c.Axis);

        depthNeg = aPlocha ? KolikDoPlochyMm : KolikDoHranyMm; // do A
        depthPos = bPlocha ? KolikDoPlochyMm : KolikDoHranyMm; // do B

        // Ak oba rovnaký typ (neurčité), predvolene A=hrana 23, B=plocha 13
        if (aPlocha == bPlocha)
        {
            depthNeg = KolikDoHranyMm;
            depthPos = KolikDoPlochyMm;
        }
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

    private static ModelVisual3D? CreatePanelFromBody(DielecModel diel, Point3D origin, bool selected, double opacity = 0.92)
    {
        MeshGeometry3D? mesh = BodySolidBuilder.TryBuild(diel, origin);
        if (mesh == null)
            return null;

        double frontOp = Math.Clamp(opacity, 0.15, 1.0);
        double backOp = Math.Clamp(opacity * 0.85, 0.12, 1.0);
        var material = new DiffuseMaterial(
            new SolidColorBrush(selected ? Colors.PanelSelected : Colors.Panel) { Opacity = frontOp });
        var back = new DiffuseMaterial(
            new SolidColorBrush(selected ? Colors.PanelSelected : Colors.Panel) { Opacity = backOp });

        return new ModelVisual3D
        {
            Content = new GeometryModel3D
            {
                Geometry = mesh,
                Material = material,
                BackMaterial = back
            }
        };
    }

    private static BoxVisual3D CreateAabbPanel(DielecModel diel, Point3D origin, bool selected, double opacity = 0.35)
    {
        double x = Math.Max(diel.RozmerX, 0.1);
        double y = Math.Max(diel.RozmerY, 0.1);
        double z = Math.Max(diel.RozmerZ, 0.1);

        return new BoxVisual3D
        {
            Center = new Point3D(origin.X + x * 0.5, origin.Y + y * 0.5, origin.Z + z * 0.5),
            Length = x,
            Width = y,
            Height = z,
            Fill = new SolidColorBrush(selected ? Colors.PanelSelected : Colors.Panel) { Opacity = Math.Clamp(opacity, 0.15, 1.0) }
        };
    }

    private static BoundingBoxVisual3D CreatePanelEdges(DielecModel diel, Point3D origin)
    {
        double x = Math.Max(diel.RozmerX, 0.1);
        double y = Math.Max(diel.RozmerY, 0.1);
        double z = Math.Max(diel.RozmerZ, 0.1);
        return new BoundingBoxVisual3D
        {
            BoundingBox = new Rect3D(origin.X, origin.Y, origin.Z, x, y, z),
            Diameter = Math.Max(Math.Min(x, Math.Min(y, z)) * 0.008, 0.6)
        };
    }

    private enum AbsWpSide { YMin, YMax, XMin, XMax }

    /// <summary>
    /// ABS hrany ako plochy len na reálnom obryse (nie cez výfrezy / prázdne úseky).
    /// </summary>
    private static IEnumerable<Visual3D> CreateAbsEdgeVisuals(
        ExportDocument doc, DielecModel diel, Point3D origin, Vector3D sceneShift)
    {
        ResolveFaceAxes(diel, out int thin, out int a0, out int a1, out bool a0IsWpX);
        double[] d = { diel.RozmerX, diel.RozmerY, diel.RozmerZ };
        double dx = a0IsWpX ? d[a0] : d[a1];
        double dy = a0IsWpX ? d[a1] : d[a0];
        double t = d[thin];
        var fill = new SolidColorBrush(Colors.Abs);
        if (fill.CanFreeze) fill.Freeze();

        var outerWp = GetAbsOuterWorkpiece(diel, dx, dy);

        var faces = new List<(Point3D C00, Point3D C10, Point3D C11, Point3D C01)>();
        void AddSide(AbsWpSide side)
        {
            foreach (var (p0, p1) in SolidSegmentsOnAabbSide(outerWp, dx, dy, side))
            {
                var f = FaceFromWp(diel, origin, thin, a0, a1, a0IsWpX, t, p0, p1);
                faces.Add(f);
            }
        }

        Point3D SideMid(AbsWpSide side) => side switch
        {
            AbsWpSide.YMin => WorkpieceToWorld(diel, origin, thin, a0, a1, a0IsWpX, dx * 0.5, 0, t * 0.5),
            AbsWpSide.YMax => WorkpieceToWorld(diel, origin, thin, a0, a1, a0IsWpX, dx * 0.5, dy, t * 0.5),
            AbsWpSide.XMin => WorkpieceToWorld(diel, origin, thin, a0, a1, a0IsWpX, 0, dy * 0.5, t * 0.5),
            _ => WorkpieceToWorld(diel, origin, thin, a0, a1, a0IsWpX, dx, dy * 0.5, t * 0.5),
        };

        bool isSuflik = diel.JeSuflik && !diel.JeSuflikPozicia;
        bool isKorpusBok = !isSuflik && (diel.JeBok
            || (diel.Nazov ?? "").Contains("bok", StringComparison.OrdinalIgnoreCase));
        bool isKorpusDnoVrch = !isSuflik && IsDnoOrVrch(diel);
        bool isTraverza = !isSuflik && IsTraverza(diel);
        bool isPolica = !isSuflik && IsPolica(diel);
        // Bok / dno / vrch / traverza / polica: AbsX = dlhé strany (x1 = vpredu).
        bool isLongShortKorpus = isKorpusBok || isKorpusDnoVrch || isTraverza || isPolica;

        AbsWpSide longA = AbsWpSide.YMin;
        AbsWpSide longB = AbsWpSide.YMax;
        AbsWpSide shortLo = AbsWpSide.XMin;
        AbsWpSide shortHi = AbsWpSide.XMax;

        if (isSuflik)
        {
            var (farSide, nearSide) = OrderAbsSidesFromSuflikDno(
                doc, diel, longA, longB, SideMid, sceneShift);
            if (diel.AbsX1 != 0) AddSide(farSide);
            if (diel.AbsX2 != 0) AddSide(nearSide);
            if (diel.AbsY1 != 0) AddSide(shortLo);
            if (diel.AbsY2 != 0) AddSide(shortHi);
        }
        else if (isLongShortKorpus)
        {
            var (frontSide, backSide) = OrderAbsSidesFrontBack(doc, longA, longB, SideMid, sceneShift);
            if (diel.AbsX1 != 0) AddSide(frontSide); // vpredu = dlhá bližšie k prednej strane
            if (diel.AbsX2 != 0) AddSide(backSide);

            if (isKorpusBok)
            {
                double zLo = SideMid(shortLo).Z;
                double zHi = SideMid(shortHi).Z;
                var bottom = zLo <= zHi ? shortLo : shortHi;
                var top = zLo <= zHi ? shortHi : shortLo;
                if (diel.AbsY1 != 0) AddSide(bottom);
                if (diel.AbsY2 != 0) AddSide(top);
            }
            else
            {
                // Dno / vrch / traverza: y1/y2 = krátke strany
                if (diel.AbsY1 != 0) AddSide(shortLo);
                if (diel.AbsY2 != 0) AddSide(shortHi);
            }
        }
        else
        {
            if (diel.AbsX1 != 0) AddSide(AbsWpSide.XMin);
            if (diel.AbsX2 != 0) AddSide(AbsWpSide.XMax);
            if (diel.AbsY1 != 0) AddSide(AbsWpSide.YMin);
            if (diel.AbsY2 != 0) AddSide(AbsWpSide.YMax);
        }

        var panelCenter = new Point3D(
            origin.X + d[0] * 0.5,
            origin.Y + d[1] * 0.5,
            origin.Z + d[2] * 0.5);

        foreach (var f in faces)
            yield return CreateAbsFaceVisual(f, panelCenter, fill);
    }

    private static (Point3D C00, Point3D C10, Point3D C11, Point3D C01) FaceFromWp(
        DielecModel diel, Point3D origin,
        int thin, int a0, int a1, bool a0IsWpX, double t,
        (double X, double Y) p0, (double X, double Y) p1)
    {
        var c00 = WorkpieceToWorld(diel, origin, thin, a0, a1, a0IsWpX, p0.X, p0.Y, 0);
        var c10 = WorkpieceToWorld(diel, origin, thin, a0, a1, a0IsWpX, p1.X, p1.Y, 0);
        var c11 = WorkpieceToWorld(diel, origin, thin, a0, a1, a0IsWpX, p1.X, p1.Y, t);
        var c01 = WorkpieceToWorld(diel, origin, thin, a0, a1, a0IsWpX, p0.X, p0.Y, t);
        return (c00, c10, c11, c01);
    }

    /// <summary>Obrys dielca vo workpiece X/Y (s výrezmi); fallback = plný obdĺžnik.</summary>
    private static List<(double X, double Y)> GetAbsOuterWorkpiece(DielecModel diel, double dx, double dy)
    {
        if (AabbVyrezMeshBuilder.TryBuildFaceProfile(diel, out var outerFace, out _, out _))
        {
            var wp = outerFace
                .Select(p => FaceCoordsToWorkpiece(diel, p.X, p.Y))
                .Select(p => (X: Math.Round(p.X, 2), Y: Math.Round(p.Y, 2)))
                .ToList();
            // dedup consecutive
            var clean = new List<(double X, double Y)>();
            foreach (var p in wp)
            {
                if (clean.Count > 0
                    && Math.Abs(clean[^1].X - p.X) < 0.15
                    && Math.Abs(clean[^1].Y - p.Y) < 0.15)
                    continue;
                clean.Add(p);
            }
            if (clean.Count >= 3)
                return clean;
        }

        return new List<(double X, double Y)>
        {
            (0, 0), (dx, 0), (dx, dy), (0, dy)
        };
    }

    private static (double X, double Y) FaceCoordsToWorkpiece(DielecModel diel, double faceX, double faceY)
    {
        ResolveFaceAxes(diel, out _, out _, out _, out bool a0IsWpX);
        return a0IsWpX ? (faceX, faceY) : (faceY, faceX);
    }

    /// <summary>
    /// Úseky obrysu ležiace na danej AABB strane — výfrez vytvorí medzeru (bez ABS).
    /// </summary>
    private static List<((double X, double Y) A, (double X, double Y) B)> SolidSegmentsOnAabbSide(
        List<(double X, double Y)> outer, double dx, double dy, AbsWpSide side)
    {
        const double eps = 1.25;
        var segs = new List<((double X, double Y) A, (double X, double Y) B)>();
        if (outer.Count < 2) return segs;

        bool Near(double a, double b) => Math.Abs(a - b) <= eps;

        for (int i = 0; i < outer.Count; i++)
        {
            var a = outer[i];
            var b = outer[(i + 1) % outer.Count];
            bool on = side switch
            {
                AbsWpSide.YMin => Near(a.Y, 0) && Near(b.Y, 0),
                AbsWpSide.YMax => Near(a.Y, dy) && Near(b.Y, dy),
                AbsWpSide.XMin => Near(a.X, 0) && Near(b.X, 0),
                AbsWpSide.XMax => Near(a.X, dx) && Near(b.X, dx),
                _ => false
            };
            if (!on) continue;

            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (len < 0.8) continue;
            segs.Add((a, b));
        }

        // Bez úseku na strane = celá strana odrezaná výfrezom → žiadne ABS.
        return segs;
    }

    private static (AbsWpSide FarFromDno, AbsWpSide NearDno) OrderAbsSidesFromSuflikDno(
        ExportDocument doc,
        DielecModel diel,
        AbsWpSide sideA,
        AbsWpSide sideB,
        Func<AbsWpSide, Point3D> mid,
        Vector3D sceneShift)
    {
        double dnoZ = ResolveSuflikDnoZ(doc, diel);
        double Dist(AbsWpSide s) => Math.Abs(mid(s).Z + sceneShift.Z - dnoZ);
        return Dist(sideA) >= Dist(sideB) ? (sideA, sideB) : (sideB, sideA);
    }

    private static (AbsWpSide Front, AbsWpSide Back) OrderAbsSidesFrontBack(
        ExportDocument doc,
        AbsWpSide sideA,
        AbsWpSide sideB,
        Func<AbsWpSide, Point3D> mid,
        Vector3D sceneShift)
    {
        if (!SuflikCeloNormalizer.TryGetFrontBack(doc, out int axis, out _, out double front))
            return (sideA, sideB); // bez referencie: YMin = wpY=0 (typicky predok AABB)

        double Coord(AbsWpSide s)
        {
            var m = mid(s);
            return axis == 0 ? m.X + sceneShift.X
                : axis == 1 ? m.Y + sceneShift.Y
                : m.Z + sceneShift.Z;
        }

        return Math.Abs(Coord(sideA) - front) <= Math.Abs(Coord(sideB) - front)
            ? (sideA, sideB)
            : (sideB, sideA);
    }

    private static ModelVisual3D CreateAbsFaceVisual(
        (Point3D C00, Point3D C10, Point3D C11, Point3D C01) f,
        Point3D panelCenter,
        Brush fill)
    {
        var u = f.C10 - f.C00;
        var v = f.C01 - f.C00;
        var n = Vector3D.CrossProduct(u, v);
        if (n.LengthSquared > 1e-8)
        {
            n.Normalize();
            var mid = new Point3D(
                (f.C00.X + f.C10.X + f.C11.X + f.C01.X) * 0.25,
                (f.C00.Y + f.C10.Y + f.C11.Y + f.C01.Y) * 0.25,
                (f.C00.Z + f.C10.Z + f.C11.Z + f.C01.Z) * 0.25);
            if (Vector3D.DotProduct(n, mid - panelCenter) < 0)
                n = -n;
            const double lift = 1.2;
            f = (
                f.C00 + n * lift,
                f.C10 + n * lift,
                f.C11 + n * lift,
                f.C01 + n * lift);
        }

        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { f.C00, f.C10, f.C11, f.C01 },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3, 0, 3, 2, 0, 2, 1 }
        };

        // Emissive = farba bez závislosti na svetle (Diffuse často vyšlo čierne).
        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(fill));
        mat.Children.Add(new EmissiveMaterial(fill));

        return new ModelVisual3D
        {
            Content = new GeometryModel3D
            {
                Geometry = mesh,
                Material = mat,
                BackMaterial = mat
            }
        };
    }

    private static double ResolveSuflikDnoZ(ExportDocument doc, DielecModel diel)
    {
        var poz = SuflikContactBuilder.FindParentPozicia(doc, diel);
        var siblings = doc.SuflikDiely
            .Where(d => !d.JeSuflikPozicia)
            .Where(d => poz == null || ReferenceEquals(SuflikContactBuilder.FindParentPozicia(doc, d), poz))
            .ToList();

        var dno = siblings.FirstOrDefault(d =>
            (d.SufelTypDielu ?? "").Equals("dno", StringComparison.OrdinalIgnoreCase)
            || (d.Nazov ?? "").Contains("dno", StringComparison.OrdinalIgnoreCase));

        if (dno != null)
            return dno.WcsMinZ + dno.RozmerZ * 0.5;

        if (poz != null)
            return poz.WcsMinZ;

        return diel.WcsMinZ;
    }

    /// <summary>Dno / vrch korpusu.</summary>
    private static bool IsDnoOrVrch(DielecModel diel)
    {
        string n = (diel.Nazov ?? "").ToLowerInvariant();
        string typ = (diel.SufelTypDielu ?? "").ToLowerInvariant();
        if (n.Contains("dno") || typ == "dno") return true;
        if (n.Contains("vrch") || n.Contains("horna") || n.Contains("horná") || typ == "vrch")
            return true;
        return false;
    }

    private static bool IsTraverza(DielecModel diel)
    {
        string n = (diel.Nazov ?? "").ToLowerInvariant();
        return n.Contains("traverz");
    }

    private static bool IsPolica(DielecModel diel)
    {
        string n = (diel.Nazov ?? "").ToLowerInvariant();
        return n.Contains("polic");
    }

    private static void ResolveFaceAxes(
        DielecModel diel, out int thin, out int a0, out int a1, out bool a0IsWpX)
    {
        double[] d = { diel.RozmerX, diel.RozmerY, diel.RozmerZ };
        thin = 0;
        if (d[1] < d[0]) thin = 1;
        if (d[2] < d[thin]) thin = 2;

        a0 = -1;
        a1 = -1;
        for (int i = 0; i < 3; i++)
        {
            if (i == thin) continue;
            if (a0 < 0) a0 = i;
            else a1 = i;
        }

        // Workpiece X = prvá plošná os AABB, okrem otočenia kvôli Y>1350.
        a0IsWpX = !PartRules.WorkpieceSwapsFaceAxes(diel);
    }

    private static Point3D WorkpieceToWorld(
        DielecModel diel,
        Point3D origin,
        int thin, int a0, int a1, bool a0IsWpX,
        double wpX, double wpY, double depth)
    {
        double[] c = { 0, 0, 0 };
        if (a0IsWpX)
        {
            c[a0] = wpX;
            c[a1] = wpY;
        }
        else
        {
            c[a1] = wpX;
            c[a0] = wpY;
        }
        c[thin] = depth;
        return new Point3D(origin.X + c[0], origin.Y + c[1], origin.Z + c[2]);
    }

    private static SphereVisual3D CreateVertex(Point3D origin, BodModel b)
    {
        return new SphereVisual3D
        {
            Center = new Point3D(origin.X + b.PosX, origin.Y + b.PosY, origin.Z + b.PosZ),
            Radius = 2.5,
            Fill = new SolidColorBrush(Colors.Vertex)
        };
    }

    /// <summary>
    /// CNC značenie (vrátane podperiek) ako valčeky cez hrúbku.
    /// GEN: značenia z generátora sa v scéne nekreslia (zbytočne ťažké).
    /// </summary>
    private static void AddCncMarkVisuals(
        List<Visual3D> list, DielecModel diel, Point3D origin, Color? overrideColor)
    {
        foreach (var z in diel.CncZnacenia)
        {
            if (!string.IsNullOrEmpty(z.Handle)
                && z.Handle.StartsWith("GEN:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Hranové diery závesov (do dvierok) — v 3D aj XCS ich ignorujeme.
            if (CncZnacenieTyp.IsAnyZaves(z) && PartRules.IsOutsideAabbFace(diel, z.PosX, z.PosY))
                continue;

            foreach (var v in CreateCncMarkVisuals(diel, origin, z, overrideColor))
                list.Add(v);
        }
    }

    private static IEnumerable<Visual3D> CreateCncMarkVisuals(
        DielecModel diel, Point3D origin, CncZnacenieModel z, Color? overrideColor)
    {
        // Priemer 1:1 z Excelu (predtým ×2 + min 6 mm — na dne/nohách príliš veľké).
        double realDia = Math.Max(z.Priemer, 1.0);
        string typ = z.ResolvedTyp;
        bool isNohyOrMovento =
            typ.Equals(CncZnacenieTyp.Nohy, StringComparison.OrdinalIgnoreCase)
            || typ.Equals(CncZnacenieTyp.Movento, StringComparison.OrdinalIgnoreCase);
        double drawDia = isNohyOrMovento
            ? realDia
            : Math.Max(realDia, 2.0);

        Point3D local = FaceToLocal(diel, z.PosX, z.PosY, z.PosZ);
        var center = new Point3D(origin.X + local.X, origin.Y + local.Y, origin.Z + local.Z);

        // Závesy majú diery do plochy (cez hrúbku) aj do hrany (mimo AABB → os face X/Y).
        Vector3D axis = MarkDrillAxis(diel, z.PosX, z.PosY, z.PosZ);
        double thin = Math.Min(diel.RozmerX, Math.Min(diel.RozmerY, diel.RozmerZ));
        double hlbka = z.Hlbka > 0.5 ? z.Hlbka : 0;
        bool edge = IsOutsideFace(diel, z.PosX, z.PosY);
        double depth = edge && hlbka > 0.5
            ? Math.Max(hlbka + 2.0, 8.0)
            : Math.Max(thin + 2.0, 6.0);
        var p1 = center - axis * (depth * 0.5);
        var p2 = center + axis * (depth * 0.5);

        Color pipeColor = overrideColor ?? Colors.CncZnacenie;

        if (overrideColor.HasValue)
        {
            // Celá skrinka: emissive valček v skutočnom Ø, bez gule
            yield return CreateEmissiveCylinder(p1, p2, drawDia * 0.5, pipeColor);
        }
        else
        {
            yield return new PipeVisual3D
            {
                Point1 = p1,
                Point2 = p2,
                Diameter = drawDia,
                Fill = OpaqueBrush(pipeColor)
            };
        }
    }

    /// <summary>
    /// Os vŕtania CNC značky: cez hrúbku, alebo do hrany ak Pos X/Y je mimo plochy.
    /// (Závesy: 2× face + 2× edge — edge má typicky X≈−4.5 mimo plátu.)
    /// </summary>
    private static Vector3D MarkDrillAxis(DielecModel diel, double faceX, double faceY, double depth)
    {
        GetFaceAxes(diel, out int thick, out int faceXi, out int faceYi, out double faceW, out double faceH);
        _ = depth;

        const double tol = 2.0;
        double outX = OutsideAmount(faceX, faceW);
        double outY = OutsideAmount(faceY, faceH);
        if (outX > tol || outY > tol)
        {
            int axis = outX >= outY ? faceXi : faceYi;
            return AxisUnit(axis);
        }

        return AxisUnit(thick);
    }

    private static bool IsOutsideFace(DielecModel diel, double faceX, double faceY)
        => PartRules.IsOutsideAabbFace(diel, faceX, faceY);

    private static double OutsideAmount(double v, double size)
    {
        if (v < 0) return -v;
        if (v > size) return v - size;
        return 0;
    }

    private static void GetFaceAxes(
        DielecModel diel,
        out int thick,
        out int faceXi,
        out int faceYi,
        out double faceW,
        out double faceH)
    {
        double sx = diel.RozmerX, sy = diel.RozmerY, sz = diel.RozmerZ;
        thick = 0;
        if (sy < sx) thick = 1;
        if (sz < (thick == 0 ? sx : sy)) thick = 2;

        faceXi = faceYi = -1;
        faceW = faceH = 0;
        bool first = true;
        for (int i = 0; i < 3; i++)
        {
            if (i == thick) continue;
            double size = i switch { 0 => sx, 1 => sy, _ => sz };
            if (first)
            {
                faceXi = i;
                faceW = size;
                first = false;
            }
            else
            {
                faceYi = i;
                faceH = size;
            }
        }
    }

    private static Vector3D AxisUnit(int axis) => axis switch
    {
        1 => new Vector3D(0, 1, 0),
        2 => new Vector3D(0, 0, 1),
        _ => new Vector3D(1, 0, 0)
    };

    private static IEnumerable<Visual3D> CreatePrienikDrillVisuals(
        DielecModel diel, Point3D origin, PrienikModel d, Color color)
    {
        Point3D local = FaceToLocal(diel, d.PosX, d.PosY, d.PosZ);
        var center = new Point3D(origin.X + local.X, origin.Y + local.Y, origin.Z + local.Z);
        Vector3D axis = ThicknessAxis(diel);
        double thin = Math.Min(diel.RozmerX, Math.Min(diel.RozmerY, diel.RozmerZ));

        bool hranata = (d.Typ ?? "").Contains("hranat", StringComparison.OrdinalIgnoreCase);
        double drawDia = hranata
            ? Math.Max(Math.Max(d.Sirka, d.Vyska), 2.0)
            : Math.Max(d.Priemer, 1.0);
        double depth = Math.Max(thin + 2.0, 6.0);

        yield return CreateEmissiveCylinder(
            center - axis * (depth * 0.5),
            center + axis * (depth * 0.5),
            drawDia * 0.5,
            color);
    }

    private static IEnumerable<Visual3D> CreateVyrezDieraVisuals(
        DielecModel diel, Point3D origin, VyrezModel v, Color color)
    {
        Point3D local = FaceToLocal(diel, v.PosX, v.PosY, v.PosZ);
        var center = new Point3D(origin.X + local.X, origin.Y + local.Y, origin.Z + local.Z);
        Vector3D axis = ThicknessAxis(diel);
        double thin = Math.Min(diel.RozmerX, Math.Min(diel.RozmerY, diel.RozmerZ));

        string tvar = (v.Tvar ?? "").ToLowerInvariant();
        bool kruh = tvar.Contains("kruh") && v.Priemer > 0.5;
        if (!kruh && !tvar.Contains("hran") && v.Priemer > 0.5 && v.Sirka < 1 && v.Vyska < 1)
            kruh = true;

        double drawDia = kruh
            ? Math.Max(v.Priemer, 1.0)
            : Math.Max(Math.Max(v.Sirka, v.Vyska), 2.0);
        double hlbka = v.Hlbka > 0.5 ? v.Hlbka : thin;
        double depth = Math.Max(hlbka + 2.0, 6.0);

        yield return CreateEmissiveCylinder(
            center - axis * (depth * 0.5),
            center + axis * (depth * 0.5),
            drawDia * 0.5,
            color);
    }

    private static Point3D FaceToLocal(DielecModel diel, double faceX, double faceY, double depth)
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

    private static Vector3D ThicknessAxis(DielecModel diel)
    {
        GetFaceAxes(diel, out int thick, out _, out _, out _, out _);
        return AxisUnit(thick);
    }
}
