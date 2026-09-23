using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Generovanie .xcs — kolíky, skrutky, movento/podperky, výrezy/zafrezy.
/// Dno↔bok / Vrch↔bok: režim A / B podľa plocha/hrana.
/// Priecka↔bok: priecka vždy hrana, bok vždy plocha.
/// Chrbat↔bok: bok = Back hrana 23; chrbat = Top plocha 12.
/// Traverza predná pod vrchom: traverza=Left/Right/Back hrana; bok/vrch=Top plocha.
/// Dno/vrch↔chrbat: A) dno/vrch=Back hrana, chrbat=Top plocha | B) dno/vrch=Top plocha, chrbat=hrana.
/// ABS poznámka: 4 hrany → „ABS 4x“; Obeh makrá z Excel AbsX1/Y1/X2/Y2.
/// Od predu: UI + odsadenie dotyku; Zo stredu = stred tohto dielca, rovnaká rozteč.
/// </summary>
internal static class XcsProgramGenerator
{
    private static readonly CultureInfo I = CultureInfo.InvariantCulture;

    private const double KolikPriemerMm = 8.0;
    private const double SkrutkaPriemerMm = 3.0;
    private const double KolikDoPlochyMm = 12.0;
    private const double KolikDoHranyMm = 23.0;
    private const double PatternStepMm = 32.0;
    /// <summary>Predznačenie Ø3 (movento, závesy, …) — nie podperky.</summary>
    private const double PredznacenieHlbkaMm = 3.5;
    private const double MoventoPatternPitchMm = 224.0;
    private const double PodperkyHlbkaMm = 12.0; // Ø3 × 12
    private const double PodperkyPatternPitchHlbka = 222.0;
    private const double PodperkyPatternPitchVyska = 32.0;
    private const double PodperkyShelfGapMm = 64.0;
    private const double ZavesPatternPitchMm = 32.0;

    public static int GenerateAll(ExportDocument doc, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        foreach (var old in Directory.EnumerateFiles(outputDir, "*.xcs"))
        {
            try { File.Delete(old); }
            catch { /* locked */ }
        }

        int files = 0;
        var korpusGroups = doc.Diely
            .Where(d => !d.JeSuflikPozicia && !d.JeSuflik)
            .GroupBy(
                d => $"{PartRules.CncGroupKey(d, doc)}|{d.DruheUpnutie}",
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in korpusGroups)
        {
            var list = group.ToList();
            var diel = list[0];
            int kusov = list.Count;
            string baseName = SafeFileName(PartRules.CncFileBaseName(diel, kusov));

            bool druheUpnutie = diel.DruheUpnutie || HasNohyZnacenie(diel);

            if (druheUpnutie)
            {
                File.WriteAllText(
                    Path.Combine(outputDir, baseName + "_A.xcs"),
                    BuildProgram(doc, diel, UpnutieVariant.A, kusov),
                    new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(outputDir, baseName + "_B.xcs"),
                    BuildProgram(doc, diel, UpnutieVariant.B, kusov),
                    new UTF8Encoding(false));
                files += 2;
            }
            else
            {
                File.WriteAllText(
                    Path.Combine(outputDir, baseName + ".xcs"),
                    BuildProgram(doc, diel, UpnutieVariant.Single, kusov),
                    new UTF8Encoding(false));
                files++;
            }
        }

        return files;
    }

    private enum UpnutieVariant { Single, A, B }

    private static bool HasNohyZnacenie(DielecModel diel)
        => diel.CncZnacenia.Any(CncZnacenieTyp.IsNohy);

    /// <summary>
    /// A / Single: kolíky (Top) + ostatné horné operácie, ABS. Nohy nie.
    /// B: len nohy na Bottom (po otočení spodkom hore → Top).
    /// </summary>
    private static string BuildProgram(ExportDocument doc, DielecModel diel, UpnutieVariant upnutie, int kusov)
    {
        var sb = new StringBuilder();
        WriteHeader(sb, diel, upnutie, kusov);

        if (upnutie == UpnutieVariant.B)
        {
            foreach (var op in CollectNohyOps(doc, diel))
                WriteAbsoluteDrill(sb, diel, op, upnutie);
            sb.AppendLine("CreateMessage(\"End\",\"koniec\",false,false);");
            return sb.ToString();
        }

        // A / Single — prvé upnutie: priorita kolíky do Top
        WriteObehMacro(sb, diel);

        foreach (var op in CollectKolikOps(doc, diel))
            WriteDrillBlock(sb, diel, op);

        foreach (var op in CollectSkrutkyOps(doc, diel))
            WriteDrillBlock(sb, diel, op);

        foreach (var op in CollectMoventoOps(doc, diel))
            WriteAbsoluteDrill(sb, diel, op, upnutie);

        foreach (var op in CollectPodperkyOps(doc, diel))
            WriteAbsoluteDrill(sb, diel, op, upnutie);

        foreach (var op in CollectZavesOps(doc, diel))
            WriteAbsoluteDrill(sb, diel, op, upnutie);

        foreach (var op in CollectZavesRadyOps(doc, diel))
            WriteAbsoluteDrill(sb, diel, op, upnutie);

        WriteVyrezGeometry(sb, doc, diel);

        sb.AppendLine("CreateMessage(\"End\",\"koniec\",false,false);");
        return sb.ToString();
    }

    private static void WriteHeader(StringBuilder sb, DielecModel diel, UpnutieVariant upnutie, int kusov)
    {
        var (dx, dy, dz) = PartRules.For(diel).WorkpieceSize(diel);
        string pieceName = WorkpieceDisplayName(PartRules.CncFileBaseName(diel, kusov));

        sb.AppendLine("SetMachiningParameters(\"AD\",5,10,16842762,false);");
        sb.AppendLine(
            $"CreateFinishedWorkpieceBox(\"{Escape(pieceName)}\"," +
            $"{Fmt(dx)},{Fmt(dy)},{Fmt(dz)});");

        if (upnutie == UpnutieVariant.B)
            sb.AppendLine("SetWorkpieceSetupPosition(120.00,210.00,50.00,0.00);");
        else
            sb.AppendLine("SetWorkpieceSetupPosition(2.50,2.50,50.00,0.00);");

        // B: len Info „po ABS…“ (+ End na konci). Bez „OTOCIT SPODKOM HORE“.
        if (upnutie == UpnutieVariant.B)
        {
            string msg = (diel.XcsInfoMessageB ?? "").Trim();
            if (msg.Length == 0)
                msg = "po ABS - odsadit o listu - otoc";
            sb.AppendLine($"CreateMessage(\"Info\",\"{Escape(msg)}\",true,true);");
        }
        else
        {
            if (diel.OtocitSpodkomHore)
                sb.AppendLine("CreateMessage(\"Info\",\"OTOCIT SPODKOM HORE\",true,true);");

            string? msg = diel.ResolveInfoMessage(kusov);
            if (upnutie == UpnutieVariant.A)
            {
                msg = (msg ?? "").TrimEnd();
                if (!msg.EndsWith(" - druhe up. po ABS", StringComparison.OrdinalIgnoreCase))
                    msg += " - druhe up. po ABS";
            }
            if (!string.IsNullOrWhiteSpace(msg))
                sb.AppendLine($"CreateMessage(\"Info\",\"{Escape(msg)}\",true,true);");
        }

        sb.AppendLine();
    }

    private static void WriteObehMacro(StringBuilder sb, DielecModel diel)
    {
        if (!diel.MaAbs) return;
        // Obeh = Excel AbsX1/Y1/X2/Y2, nie text CreateMessage.
        diel.GetAbsSideFlags(out bool vpredu, out bool vlavo, out bool vpravo, out bool vzadu);
        if (!vpredu && !vlavo && !vpravo && !vzadu) return;

        double absMm = diel.AbsPaskaMm > 0 ? diel.AbsPaskaMm : 0.8;
        sb.AppendLine($"SetMacroParam(\"ABS\",{MacroStr(absMm)});");
        sb.AppendLine($"SetMacroParam(\"vpredu\",{(vpredu ? "true" : "false")});");
        sb.AppendLine($"SetMacroParam(\"vlavo\",{(vlavo ? "true" : "false")});");
        sb.AppendLine($"SetMacroParam(\"vpravo\",{(vpravo ? "true" : "false")});");
        sb.AppendLine($"SetMacroParam(\"vzadu\",{(vzadu ? "true" : "false")});");
        sb.AppendLine("CreateMacro(\"Obeh_novy_DTD\",\"Obeh_novy_DTD\");");
        sb.AppendLine();
    }

    /// <summary>
    /// Kolíky z ručných sérií:
    /// • dno ↔ bok L/P (režim A/B podľa plocha/hrana)
    /// • vrch ↔ bok L/P (režim A/B — zrkadlo dna, ale A/B opačne pri vrchu)
    /// • priecka ↔ bok L/P (priecka vždy hrana, bok vždy plocha)
    /// • chrbat ↔ bok L/P (bok=Back hrana, chrbat=Top plocha; 1. séria od dna, 2. od vrchu)
    /// • dno/vrch ↔ chrbat (A/B podľa plocha/hrana; Zo stredu = stred dielca)
    /// • traverza predná pod vrchom ↔ bok L/P / vrch
    /// </summary>
    private static List<DrillOp> CollectKolikOps(ExportDocument doc, DielecModel diel)
    {
        var ops = new List<DrillOp>();
        string selfRole = RoleOf(doc, diel);
        if (selfRole is not ("dno" or "vrch" or "bokL" or "bokP" or "priecka" or "chrbat" or "traverza"))
            return ops;

        foreach (var c in doc.Dotyky)
        {
            if (c.JeSuflikAuto || !c.MaKoliky)
                continue;
            if (!JeVonkajsiKorpusDotyk(doc, c))
                continue;

            bool isA = string.Equals(c.PartA, diel.Nazov, StringComparison.OrdinalIgnoreCase);
            bool isB = string.Equals(c.PartB, diel.Nazov, StringComparison.OrdinalIgnoreCase);
            if (!isA && !isB)
                continue;

            string partnerName = isA ? c.PartB : c.PartA;
            string partnerRole = RoleOf(doc, partnerName);

            var partA = Find(doc, c.PartA);
            var partB = Find(doc, c.PartB);
            ResolvePlocha(c, partA, partB, out _, out _, out DielecModel? plocha);
            bool tentoJePlocha = plocha != null &&
                string.Equals(plocha.Nazov, diel.Nazov, StringComparison.OrdinalIgnoreCase);

            if (IsPrieckaBokPair(selfRole, partnerRole)
                || IsDnoBokPair(selfRole, partnerRole)
                || IsVrchBokPair(selfRole, partnerRole))
            {
                if (!TryResolveContactDrillContext(doc, diel, c, selfRole, out var ctx))
                    continue;

                foreach (var serie in c.KolikSerie)
                {
                    if (serie.PocetKolikov < 1)
                        continue;

                    int n = serie.Cislo > 0 ? serie.Cislo : 1;
                    int refPos = serie.ZoStredu
                        ? ctx.Orient.RefPos
                        : ApplyZDruhejRef(ctx.Orient.RefPos, serie.ZDruhejStrany);

                    double odPredu = ResolveOdPredu(diel, c, serie);

                    string opName = ContactOpName(doc, "koliky", ctx.PartnerName, n);
                    ops.Add(new DrillOp(
                        IsEdge: !ctx.TentoJePlocha,
                        PatternAlongPrimary: ctx.Orient.PatternAlongPrimary,
                        Workplane: ctx.Orient.Workplane,
                        RefPos: refPos,
                        Id: opName,
                        Description: opName,
                        Pocet: serie.PocetKolikov,
                        OdPredu: odPredu,
                        Roztec: serie.RoztecKolikov,
                        Depth: ctx.TentoJePlocha ? KolikDoPlochyMm : KolikDoHranyMm,
                        Dia: KolikPriemerMm,
                        OrthoPos: ctx.TentoJePlocha ? ctx.FaceOrtho : null));
                }

                continue;
            }

            if (IsTraverzaBokPair(selfRole, partnerRole) || IsTraverzaVrchPair(selfRole, partnerRole))
            {
                if (!TryGetTraverzaKind(doc, out var kind) || kind != TraverzaKind.PrednaPodVrchom)
                    continue;
                if (!TryResolveTraverzaPrednaPodVrchom(selfRole, partnerRole, out var orient, out bool isEdge))
                    continue;

                // Bok/vrch: Top plocha hĺbka 12, ale IsEdge súradnice x=odPredu y=halfThin (nie ortho/odPredu).
                tentoJePlocha = selfRole is "bokL" or "bokP" or "vrch";

                foreach (var serie in c.KolikSerie)
                {
                    if (serie.PocetKolikov < 1)
                        continue;

                    int n = serie.Cislo > 0 ? serie.Cislo : 1;
                    double odPredu = ResolveOdPredu(diel, c, serie);
                    string opName = ContactOpName(doc, "koliky", partnerName, n);
                    ops.Add(new DrillOp(
                        IsEdge: isEdge,
                        PatternAlongPrimary: orient.PatternAlongPrimary,
                        Workplane: orient.Workplane,
                        RefPos: orient.RefPos,
                        Id: opName,
                        Description: opName,
                        Pocet: serie.PocetKolikov,
                        OdPredu: odPredu,
                        Roztec: serie.RoztecKolikov,
                        Depth: tentoJePlocha ? KolikDoPlochyMm : KolikDoHranyMm,
                        Dia: KolikPriemerMm));
                }

                continue;
            }

            if (IsChrbatBokPair(selfRole, partnerRole))
            {
                if (!TryGetChrbatLayout(doc, out var chrbatLayout))
                    continue;

                // Bok: Back (hrana 23). Chrbat: Top (plocha 12).
                foreach (var serie in c.KolikSerie)
                {
                    if (serie.PocetKolikov < 1)
                        continue;

                    bool odVrchu = serie.ZDruhejStrany && !serie.ZoStredu;
                    if (!TryResolveChrbatBok(selfRole, partnerRole, odVrchu, chrbatLayout,
                            out var chrbatOrient, out bool isEdge))
                        continue;

                    int n = serie.Cislo > 0 ? serie.Cislo : 1;
                    double odPredu = ResolveOdPredu(diel, c, serie);
                    bool tentoJeBok = selfRole is "bokL" or "bokP";

                    string opName = ContactOpName(doc, "koliky", partnerName, n);
                    ops.Add(new DrillOp(
                        IsEdge: isEdge,
                        PatternAlongPrimary: chrbatOrient.PatternAlongPrimary,
                        Workplane: chrbatOrient.Workplane,
                        RefPos: chrbatOrient.RefPos,
                        Id: opName,
                        Description: opName,
                        Pocet: serie.PocetKolikov,
                        OdPredu: odPredu,
                        Roztec: serie.RoztecKolikov,
                        Depth: tentoJeBok ? KolikDoHranyMm : KolikDoPlochyMm,
                        Dia: KolikPriemerMm));
                }

                continue;
            }
            else if (IsDnoOrVrchChrbatPair(selfRole, partnerRole))
            {
                if (!TryGetChrbatLayout(doc, out var layout))
                    continue;

                // A: chrbat=plocha, dno/vrch=hrana | B: chrbat=hrana, dno/vrch=plocha
                bool modeA = (selfRole == "chrbat" && tentoJePlocha)
                    || ((selfRole is "dno" or "vrch") && !tentoJePlocha);
                bool modeB = (selfRole == "chrbat" && !tentoJePlocha)
                    || ((selfRole is "dno" or "vrch") && tentoJePlocha);
                if (!modeA && !modeB)
                    continue;

                double? faceOrthoDv = null;
                if (tentoJePlocha
                    && selfRole is "dno" or "vrch"
                    && TryContactOrthoOnWorkpiece(diel, c, out double orthoDv))
                    faceOrthoDv = orthoDv;

                foreach (var serie in c.KolikSerie)
                {
                    if (serie.PocetKolikov < 1)
                        continue;

                    bool odBokuP = serie.ZDruhejStrany && !serie.ZoStredu;
                    DrillOrient orientDc;
                    bool isEdge;
                    if (modeA)
                    {
                        if (!TryResolveDnoVrchChrbatModeA(selfRole, partnerRole, layout,
                                serie.ZoStredu, odBokuP, out orientDc, out isEdge))
                            continue;
                    }
                    else
                    {
                        if (!TryResolveDnoVrchChrbatModeB(selfRole, partnerRole, layout,
                                serie.ZoStredu, odBokuP, out orientDc, out isEdge))
                            continue;
                    }

                    int n = serie.Cislo > 0 ? serie.Cislo : 1;
                    double odPredu = ResolveOdPredu(diel, c, serie);
                    bool topChrbatDnoVrch = modeB && selfRole is "dno" or "vrch" && partnerRole == "chrbat";

                    string opName = ContactOpName(doc, "koliky", partnerName, n);
                    ops.Add(new DrillOp(
                        IsEdge: isEdge,
                        PatternAlongPrimary: orientDc.PatternAlongPrimary,
                        Workplane: orientDc.Workplane,
                        RefPos: orientDc.RefPos,
                        Id: opName,
                        Description: opName,
                        Pocet: serie.PocetKolikov,
                        OdPredu: odPredu,
                        Roztec: serie.RoztecKolikov,
                        Depth: topChrbatDnoVrch || tentoJePlocha ? KolikDoPlochyMm : KolikDoHranyMm,
                        Dia: KolikPriemerMm,
                        OrthoPos: tentoJePlocha && !topChrbatDnoVrch ? faceOrthoDv : null));
                }

                continue;
            }

            continue;
        }

        EnsureUniqueNames(ops);
        return ops;
    }

    /// <summary>
    /// Skrutky — rovnaká workplane / pattern ako kolíky, len do plochy dielca.
    /// Dno×bokL/P: Top RefPos 0/2, pattern (n,1,roztec,32) — 1:1 ako kolíky.
    /// Vrch×bokL/P: Top RefPos 2/0, pattern (n,1,roztec,32) — 1:1 ako kolíky.
    /// BokL×dno: Top RefPos 2 | BokL×vrch: Top RefPos 0.
    /// Chrbat×bok: Top na chrbate (ako kolíky); bok = len kolíky do hrany Back.
    /// Dno/vrch×chrbat (režim B): Top RefPos 1/3 (dno), 3/1 (vrch) — ako kolíky.
    /// Chrbat×dno/vrch (režim A): Top na chrbate — ako kolíky.
    /// </summary>
    private static List<DrillOp> CollectSkrutkyOps(ExportDocument doc, DielecModel diel)
    {
        var ops = new List<DrillOp>();
        string selfRole = RoleOf(doc, diel);
        if (selfRole is not ("dno" or "vrch" or "bokL" or "bokP" or "priecka" or "chrbat" or "traverza"))
            return ops;

        double thickness = ThinDimension(diel);
        double depth = thickness > 1 ? thickness : 18;

        foreach (var c in doc.Dotyky)
        {
            if (c.JeSuflikAuto || !c.MaSkrutky)
                continue;
            if (!JeVonkajsiKorpusDotyk(doc, c))
                continue;

            bool isA = string.Equals(c.PartA, diel.Nazov, StringComparison.OrdinalIgnoreCase);
            bool isB = string.Equals(c.PartB, diel.Nazov, StringComparison.OrdinalIgnoreCase);
            if (!isA && !isB)
                continue;

            string partnerName = isA ? c.PartB : c.PartA;
            string partnerRole = RoleOf(doc, partnerName);

            var partA = Find(doc, c.PartA);
            var partB = Find(doc, c.PartB);
            ResolvePlocha(c, partA, partB, out _, out _, out DielecModel? plocha);
            bool tentoJePlocha = plocha != null &&
                string.Equals(plocha.Nazov, diel.Nazov, StringComparison.OrdinalIgnoreCase);

            if (IsDnoOrVrchChrbatPair(selfRole, partnerRole))
            {
                if (!tentoJePlocha)
                    continue;
                CollectSkrutkyDnoVrchChrbat(doc, diel, c, selfRole, partnerName, partnerRole,
                    thickness, depth, ops);
                continue;
            }

            if (IsChrbatBokPair(selfRole, partnerRole))
            {
                // Skrutky len do plochy chrbáta (Top); bok ostáva hrana Back — len kolíky.
                if (selfRole != "chrbat")
                    continue;
                CollectSkrutkyChrbatBok(doc, diel, c, partnerName, partnerRole, depth, ops);
                continue;
            }

            // Dno/vrch ↔ bok: skrutky len do plochy kontaktu (nie do hrany).
            if (IsDnoBokPair(selfRole, partnerRole) || IsVrchBokPair(selfRole, partnerRole))
            {
                CollectSkrutkyDnoVrchBok(doc, diel, c, selfRole, depth, ops);
                continue;
            }

            if (IsTraverzaBokPair(selfRole, partnerRole) || IsTraverzaVrchPair(selfRole, partnerRole))
            {
                if (!TryGetTraverzaKind(doc, out var kind) || kind != TraverzaKind.PrednaPodVrchom)
                    continue;
                // Skrutky len do plochy (bok / vrch), nie do hrany traverzy.
                if (selfRole == "traverza")
                    continue;
                if (!TryResolveTraverzaPrednaPodVrchom(selfRole, partnerRole, out var travOrient, out _))
                    continue;

                foreach (var serie in c.SkrutkySerie)
                {
                    if (serie.PocetSkrutiek < 1)
                        continue;

                    double roztec = serie.RoztecSkrutiek;
                    double odPreduUi = serie.OdPredu;
                    if (serie.Symetricke)
                    {
                        if (!SkrutkaLayout.TryResolveSymetricParams(c, serie, out double len, out double odMin, out _))
                            continue;
                        if (!SkrutkaLayout.TryComputeSymetricRoztec(
                                len,
                                serie.SymetriaMedziKolikmi ? serie.OdPredu : odMin,
                                serie.PocetSkrutiek, out roztec, out _))
                            continue;
                        odPreduUi = odMin;
                    }

                    int n = serie.Cislo > 0 ? serie.Cislo : 1;
                    double odPredu = AdjustOdPreduForPart(
                        diel, c, odPreduUi, serie.ZDruhejStrany && !serie.Symetricke);
                    string opName = ContactOpName(doc, "skrutky", partnerName, n);
                    ops.Add(new DrillOp(
                        IsEdge: true,
                        PatternAlongPrimary: travOrient.PatternAlongPrimary,
                        Workplane: travOrient.Workplane,
                        RefPos: travOrient.RefPos,
                        Id: opName,
                        Description: opName,
                        Pocet: serie.PocetSkrutiek,
                        OdPredu: odPredu,
                        Roztec: roztec,
                        Depth: depth,
                        Dia: SkrutkaPriemerMm,
                        OrthoPos: null,
                        Tool: "-1"));
                }

                continue;
            }

            if (!TryResolveContactDrillContext(doc, diel, c, selfRole, out var ctx))
                continue;
            if (!ctx.TentoJePlocha)
                continue;

            DrillOrient orient = ctx.Orient;
            int refPosBase = orient.RefPos;
            double? ortho = ctx.FaceOrtho;
            // Top/Bottom plocha: RefPos L/P ostáva (ako kolíky) — Z druhej strany len OdPredu.
            bool faceTop = IsTopOrBottomWorkplane(orient.Workplane)
                || string.IsNullOrWhiteSpace(orient.Workplane);

            foreach (var serie in c.SkrutkySerie)
            {
                if (serie.PocetSkrutiek < 1)
                    continue;

                double roztec = serie.RoztecSkrutiek;
                double odPreduUi = serie.OdPredu;
                if (serie.Symetricke)
                {
                    if (!SkrutkaLayout.TryResolveSymetricParams(c, serie, out double len, out double odMin, out _))
                        continue;
                    if (!SkrutkaLayout.TryComputeSymetricRoztec(
                            len,
                            serie.SymetriaMedziKolikmi ? serie.OdPredu : odMin,
                            serie.PocetSkrutiek, out roztec, out _))
                        continue;
                    odPreduUi = odMin;
                }

                int n = serie.Cislo > 0 ? serie.Cislo : 1;
                int refPos = faceTop
                    ? refPosBase
                    : ApplyZDruhejRef(refPosBase, serie.ZDruhejStrany && !serie.Symetricke);
                double odPredu = AdjustOdPreduForPart(diel, c, odPreduUi, serie.ZDruhejStrany && !serie.Symetricke);

                string opName = ContactOpName(doc, "skrutky", ctx.PartnerName, n);
                string wp = string.IsNullOrWhiteSpace(orient.Workplane) ? "Top" : orient.Workplane;
                ops.Add(new DrillOp(
                    IsEdge: false,
                    PatternAlongPrimary: faceTop || orient.PatternAlongPrimary,
                    Workplane: wp,
                    RefPos: refPos,
                    Id: opName,
                    Description: opName,
                    Pocet: serie.PocetSkrutiek,
                    OdPredu: odPredu,
                    Roztec: roztec,
                    Depth: depth,
                    Dia: SkrutkaPriemerMm,
                    OrthoPos: ortho,
                    Tool: "-1"));
            }
        }

        EnsureUniqueNames(ops);
        return ops;
    }

    /// <summary>
    /// Skrutky dno/vrch ↔ chrbat — rovnaká os/pattern ako kolíky, len do plochy (dno/vrch alebo chrbat).
    /// </summary>
    private static void CollectSkrutkyDnoVrchChrbat(
        ExportDocument doc, DielecModel diel, ContactMark c,
        string selfRole, string partnerName, string partnerRole,
        double thickness, double depth, List<DrillOp> ops)
    {
        if (!TryGetChrbatLayout(doc, out var layout))
            return;

        var partA = Find(doc, c.PartA);
        var partB = Find(doc, c.PartB);
        ResolvePlocha(c, partA, partB, out _, out _, out DielecModel? plocha);
        bool tentoJePlocha = plocha != null &&
            string.Equals(plocha.Nazov, diel.Nazov, StringComparison.OrdinalIgnoreCase);

        bool modeA = (selfRole == "chrbat" && tentoJePlocha)
            || ((selfRole is "dno" or "vrch") && !tentoJePlocha);
        bool modeB = (selfRole == "chrbat" && !tentoJePlocha)
            || ((selfRole is "dno" or "vrch") && tentoJePlocha);
        if (!modeA && !modeB)
            return;

        foreach (var serie in c.SkrutkySerie)
        {
            if (serie.PocetSkrutiek < 1)
                continue;

            bool odBokuP = serie.ZDruhejStrany;
            DrillOrient orientDc;
            bool isEdge;
            if (modeA)
            {
                if (!TryResolveDnoVrchChrbatModeA(selfRole, partnerRole, layout,
                        zoStredu: false, odBokuP, out orientDc, out isEdge))
                    continue;
            }
            else
            {
                if (!TryResolveDnoVrchChrbatModeB(selfRole, partnerRole, layout,
                        zoStredu: false, odBokuP, out orientDc, out isEdge))
                    continue;
            }

            double roztec = serie.RoztecSkrutiek;
            double odPreduUi = serie.OdPredu;
            if (serie.Symetricke)
            {
                if (!SkrutkaLayout.TryResolveSymetricParams(c, serie, out double len, out double odMin, out _))
                    continue;
                if (!SkrutkaLayout.TryComputeSymetricRoztec(
                        len,
                        serie.SymetriaMedziKolikmi ? serie.OdPredu : odMin,
                        serie.PocetSkrutiek, out roztec, out _))
                    continue;
                odPreduUi = odMin;
            }

            int n = serie.Cislo > 0 ? serie.Cislo : 1;
            double odPredu = AdjustOdPreduForPart(diel, c, odPreduUi, serie.ZDruhejStrany && !serie.Symetricke);

            string opName = ContactOpName(doc, "skrutky", partnerName, n);
            ops.Add(new DrillOp(
                IsEdge: isEdge,
                PatternAlongPrimary: orientDc.PatternAlongPrimary,
                Workplane: orientDc.Workplane,
                RefPos: orientDc.RefPos,
                Id: opName,
                Description: opName,
                Pocet: serie.PocetSkrutiek,
                OdPredu: odPredu,
                Roztec: roztec,
                Depth: depth,
                Dia: SkrutkaPriemerMm,
                OrthoPos: null,
                Tool: "-1"));
        }
    }

    /// <summary>
    /// Skrutky chrbat ↔ bok — len do plochy chrbáta (Top), rovnaká orientácia ako kolíky.
    /// </summary>
    private static void CollectSkrutkyChrbatBok(
        ExportDocument doc, DielecModel diel, ContactMark c,
        string partnerName, string partnerRole, double depth, List<DrillOp> ops)
    {
        if (!TryGetChrbatLayout(doc, out var layout))
            return;

        foreach (var serie in c.SkrutkySerie)
        {
            if (serie.PocetSkrutiek < 1)
                continue;

            bool odVrchu = serie.ZDruhejStrany && !serie.Symetricke;
            if (!TryResolveChrbatBok("chrbat", partnerRole, odVrchu, layout,
                    out var orient, out bool isEdge))
                continue;

            double roztec = serie.RoztecSkrutiek;
            double odPreduUi = serie.OdPredu;
            if (serie.Symetricke)
            {
                if (!SkrutkaLayout.TryResolveSymetricParams(c, serie, out double len, out double odMin, out _))
                    continue;
                if (!SkrutkaLayout.TryComputeSymetricRoztec(
                        len,
                        serie.SymetriaMedziKolikmi ? serie.OdPredu : odMin,
                        serie.PocetSkrutiek, out roztec, out _))
                    continue;
                odPreduUi = odMin;
                odVrchu = false;
            }

            int n = serie.Cislo > 0 ? serie.Cislo : 1;
            double odPredu = AdjustOdPreduForPart(diel, c, odPreduUi, serie.ZDruhejStrany && !serie.Symetricke);

            string opName = ContactOpName(doc, "skrutky", partnerName, n);
            ops.Add(new DrillOp(
                IsEdge: isEdge,
                PatternAlongPrimary: orient.PatternAlongPrimary,
                Workplane: orient.Workplane,
                RefPos: orient.RefPos,
                Id: opName,
                Description: opName,
                Pocet: serie.PocetSkrutiek,
                OdPredu: odPredu,
                Roztec: roztec,
                Depth: depth,
                Dia: SkrutkaPriemerMm,
                OrthoPos: null,
                Tool: "-1"));
        }
    }

    /// <summary>
    /// Skrutky dno/vrch ↔ bok — len do dielca, ktorý je pri kontakte plocha (Top).
    /// Do hrany sa nevŕta: Mode A → dno/vrch Top; Mode B → bok Top.
    /// Pattern CreatePattern(n,1,roztec,32) ako kolíky na ploche.
    /// </summary>
    private static void CollectSkrutkyDnoVrchBok(
        ExportDocument doc, DielecModel diel, ContactMark c,
        string selfRole, double depth, List<DrillOp> ops)
    {
        bool isA = string.Equals(c.PartA, diel.Nazov, StringComparison.OrdinalIgnoreCase);
        bool isB = string.Equals(c.PartB, diel.Nazov, StringComparison.OrdinalIgnoreCase);
        if (!isA && !isB)
            return;

        string partnerName = isA ? c.PartB : c.PartA;
        string partnerRole = RoleOf(doc, partnerName);
        if (!TryResolveSkrutkyFaceDnoVrchBok(selfRole, partnerRole, out var orient))
            return;

        // Skrutky len do plochy kontaktu — nie do hrany (dno/vrch ani bok).
        var partA = Find(doc, c.PartA);
        var partB = Find(doc, c.PartB);
        ResolvePlocha(c, partA, partB, out _, out _, out DielecModel? plocha);
        bool tentoJePlocha = plocha != null &&
            string.Equals(plocha.Nazov, diel.Nazov, StringComparison.OrdinalIgnoreCase);
        if (!tentoJePlocha)
            return;

        foreach (var serie in c.SkrutkySerie)
        {
            if (serie.PocetSkrutiek < 1)
                continue;

            double roztec = serie.RoztecSkrutiek;
            double odPreduUi = serie.OdPredu;
            if (serie.Symetricke)
            {
                if (!SkrutkaLayout.TryResolveSymetricParams(c, serie, out double len, out double odMin, out _))
                    continue;
                if (!SkrutkaLayout.TryComputeSymetricRoztec(
                        len,
                        serie.SymetriaMedziKolikmi ? serie.OdPredu : odMin,
                        serie.PocetSkrutiek, out roztec, out _))
                    continue;
                odPreduUi = odMin;
            }

            int n = serie.Cislo > 0 ? serie.Cislo : 1;
            // RefPos L/P z tabuľky plochy — Z druhej strany len posunie odPredu, nie Ref 0↔2.
            double odPredu = AdjustOdPreduForPart(
                diel, c, odPreduUi, serie.ZDruhejStrany && !serie.Symetricke);
            string opName = ContactOpName(doc, "skrutky", partnerName, n);
            ops.Add(new DrillOp(
                IsEdge: false,
                PatternAlongPrimary: true,
                Workplane: "Top",
                RefPos: orient.RefPos,
                Id: opName,
                Description: opName,
                Pocet: serie.PocetSkrutiek,
                OdPredu: odPredu,
                Roztec: roztec,
                Depth: depth,
                Dia: SkrutkaPriemerMm,
                OrthoPos: null,
                Tool: "-1"));
        }
    }

    /// <summary>
    /// Plocha dno/vrch/bok pre skrutky — rovnaké RefPos ako kolíky na Top.
    /// </summary>
    private static bool TryResolveSkrutkyFaceDnoVrchBok(
        string selfRole, string partnerRole, out DrillOrient orient)
    {
        orient = default;

        // Dno = plocha (ako kolíky Mode A)
        if (selfRole == "dno" && partnerRole == "bokL")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "dno" && partnerRole == "bokP")
        {
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: true);
            return true;
        }

        // Vrch = plocha (ako kolíky Mode B — zrkadlo dna)
        if (selfRole == "vrch" && partnerRole == "bokL")
        {
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "vrch" && partnerRole == "bokP")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: true);
            return true;
        }

        // Bok = plocha × dno (Mode B)
        if (selfRole == "bokL" && partnerRole == "dno")
        {
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "bokP" && partnerRole == "dno")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: true);
            return true;
        }

        // Bok = plocha × vrch (Mode A)
        if (selfRole == "bokL" && partnerRole == "vrch")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "bokP" && partnerRole == "vrch")
        {
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: true);
            return true;
        }

        return false;
    }

    private readonly record struct ContactDrillContext(
        DrillOrient Orient,
        bool TentoJePlocha,
        double? FaceOrtho,
        string PartnerName);

    /// <summary>Orientácia vŕtania pre dno/vrch/priečka ↔ bok (spoločné pre kolíky aj skrutky).</summary>
    private static bool TryResolveContactDrillContext(
        ExportDocument doc,
        DielecModel diel,
        ContactMark c,
        string selfRole,
        out ContactDrillContext ctx)
    {
        ctx = default;

        bool isA = string.Equals(c.PartA, diel.Nazov, StringComparison.OrdinalIgnoreCase);
        bool isB = string.Equals(c.PartB, diel.Nazov, StringComparison.OrdinalIgnoreCase);
        if (!isA && !isB)
            return false;

        string partnerName = isA ? c.PartB : c.PartA;
        string partnerRole = RoleOf(doc, partnerName);

        var partA = Find(doc, c.PartA);
        var partB = Find(doc, c.PartB);
        ResolvePlocha(c, partA, partB, out _, out _, out DielecModel? plocha);
        bool tentoJePlocha = plocha != null &&
            string.Equals(plocha.Nazov, diel.Nazov, StringComparison.OrdinalIgnoreCase);

        DrillOrient orient;
        if (IsPrieckaBokPair(selfRole, partnerRole))
        {
            tentoJePlocha = selfRole is "bokL" or "bokP";
            if (!TryResolvePrieckaBok(selfRole, partnerRole, out orient))
                return false;
        }
        else if (IsDnoBokPair(selfRole, partnerRole))
        {
            bool modeA = (selfRole == "dno" && tentoJePlocha)
                || ((selfRole is "bokL" or "bokP") && !tentoJePlocha);
            bool modeB = (selfRole == "dno" && !tentoJePlocha)
                || ((selfRole is "bokL" or "bokP") && tentoJePlocha);

            if (modeA)
            {
                if (!TryResolveDnoBokModeA(selfRole, partnerRole, diel, c, out orient))
                    return false;
            }
            else if (modeB)
            {
                if (!TryResolveDnoBokModeB(selfRole, partnerRole, out orient))
                    return false;
            }
            else
                return false;
        }
        else if (IsVrchBokPair(selfRole, partnerRole))
        {
            bool modeA = (selfRole == "vrch" && !tentoJePlocha)
                || ((selfRole is "bokL" or "bokP") && tentoJePlocha);
            bool modeB = (selfRole == "vrch" && tentoJePlocha)
                || ((selfRole is "bokL" or "bokP") && !tentoJePlocha);

            if (modeA)
            {
                if (!TryResolveVrchBokModeA(selfRole, partnerRole, out orient))
                    return false;
            }
            else if (modeB)
            {
                if (!TryResolveVrchBokModeB(selfRole, partnerRole, out orient))
                    return false;
            }
            else
                return false;
        }
        else
            return false;

        double? faceOrtho = null;
        if (tentoJePlocha
            && selfRole is "bokL" or "bokP"
            && partnerRole is "dno" or "priecka"
            && TryContactOrthoOnWorkpiece(diel, c, out double ortho))
            faceOrtho = ortho;

        ctx = new ContactDrillContext(orient, tentoJePlocha, faceOrtho, partnerName);
        return true;
    }

    private static bool IsDnoBokPair(string a, string b) =>
        (a == "dno" && b is "bokL" or "bokP")
        || (b == "dno" && a is "bokL" or "bokP");

    private static bool IsPrieckaBokPair(string a, string b) =>
        (a == "priecka" && b is "bokL" or "bokP")
        || (b == "priecka" && a is "bokL" or "bokP");

    private static bool IsTraverzaBokPair(string a, string b) =>
        (a == "traverza" && b is "bokL" or "bokP")
        || (b == "traverza" && a is "bokL" or "bokP");

    private static bool IsTraverzaVrchPair(string a, string b) =>
        (a == "traverza" && b == "vrch") || (a == "vrch" && b == "traverza");

    private enum TraverzaKind
    {
        Unknown,
        PrednaPodVrchom,
        PrednaNadDnom,
        ZadnaPodVrchom,
        ZadnaNadDnom
    }

    /// <summary>
    /// Typ traverzy podľa WCS: predok/zadok + pod vrchom / nad dnom.
    /// </summary>
    private static bool TryGetTraverzaKind(ExportDocument doc, out TraverzaKind kind)
    {
        kind = TraverzaKind.Unknown;
        var trav = doc.Diely.FirstOrDefault(d =>
            !d.JeSuflik && RoleOf(doc, d) == "traverza");
        if (trav == null)
            return false;

        var bok = doc.Diely.FirstOrDefault(d =>
            !d.JeSuflik && RoleOf(doc, d) is "bokL" or "bokP");
        var dno = doc.Diely.FirstOrDefault(d =>
            !d.JeSuflik && RoleOf(doc, d) == "dno");
        var vrch = doc.Diely.FirstOrDefault(d =>
            !d.JeSuflik && RoleOf(doc, d) == "vrch");
        var chrbat = doc.Diely.FirstOrDefault(d =>
            !d.JeSuflik && RoleOf(doc, d) == "chrbat");
        if (bok == null)
            return false;

        const double tol = 3.0;
        double frontY = bok.WcsMinY;
        double bokMaxY = bok.WcsMinY + bok.RozmerY;
        double backY = bokMaxY;
        if (chrbat != null)
        {
            double cMin = chrbat.WcsMinY;
            double cMax = chrbat.WcsMinY + chrbat.RozmerY;
            backY = Math.Abs(cMax - bokMaxY) <= Math.Abs(cMin - bokMaxY) ? cMax : cMin;
        }

        double travMinY = trav.WcsMinY;
        double travMaxY = trav.WcsMinY + trav.RozmerY;
        double travMinZ = trav.WcsMinZ;
        double travMaxZ = trav.WcsMinZ + trav.RozmerZ;

        bool nearFront = Math.Abs(travMinY - frontY) <= tol;
        bool nearBack = Math.Abs(travMaxY - backY) <= tol
            || Math.Abs(travMaxY - bokMaxY) <= tol;

        bool underVrch = vrch != null && Math.Abs(travMaxZ - vrch.WcsMinZ) <= tol;
        bool aboveDno = dno != null
            && Math.Abs(travMinZ - (dno.WcsMinZ + dno.RozmerZ)) <= tol;

        if (nearFront && underVrch) kind = TraverzaKind.PrednaPodVrchom;
        else if (nearFront && aboveDno) kind = TraverzaKind.PrednaNadDnom;
        else if (nearBack && underVrch) kind = TraverzaKind.ZadnaPodVrchom;
        else if (nearBack && aboveDno) kind = TraverzaKind.ZadnaNadDnom;
        else return false;

        return true;
    }

    /// <summary>
    /// Predná traverza pod vrchom — CNC: mate s vrchom = Back.
    /// Traverza×bokL: Left/2 | ×bokP: Right/0 | ×vrch: Back/2 — hrana, pattern (1,n,32,roztec), (odPredu,9).
    /// BokL×traverza: Top/2 | BokP: Top/0 — plocha hĺbka 12, súradnice x=odPredu y=9, pattern (1,n,32,roztec).
    /// Vrch×traverza: Top/2 — plocha, pattern (1,n,32,roztec), (odPredu,9).
    /// Od predu: UI + odsadenie styku (hrúbka materiálu cez AdjustOdPreduForPart).
    /// </summary>
    private static bool TryResolveTraverzaPrednaPodVrchom(
        string selfRole, string partnerRole,
        out DrillOrient orient, out bool isEdge)
    {
        orient = default;
        isEdge = true;

        // Vrchná traverza: Left → bok L, Right → bok P (Back = k vrchu)
        if (selfRole == "traverza" && partnerRole == "bokL")
        {
            orient = new DrillOrient("Left", 2, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "traverza" && partnerRole == "bokP")
        {
            orient = new DrillOrient("Right", 0, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "traverza" && partnerRole == "vrch")
        {
            orient = new DrillOrient("Back", 2, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "bokL" && partnerRole == "traverza")
        {
            // Top plocha, súradnice ako hrana: x=odPredu y=halfThin, pattern (1,n,32,roztec)
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "bokP" && partnerRole == "traverza")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "vrch" && partnerRole == "traverza")
        {
            // Top plocha, x=odPredu y=9
            isEdge = true;
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: false);
            return true;
        }

        return false;
    }

    private static bool IsVrchBokPair(string a, string b) =>
        (a == "vrch" && b is "bokL" or "bokP")
        || (b == "vrch" && a is "bokL" or "bokP");

    private static bool IsChrbatBokPair(string a, string b) =>
        (a == "chrbat" && b is "bokL" or "bokP")
        || (b == "chrbat" && a is "bokL" or "bokP");

    private static bool IsDnoOrVrchChrbatPair(string a, string b) =>
        ((a is "dno" or "vrch") && b == "chrbat")
        || (a == "chrbat" && (b is "dno" or "vrch"));

    /// <summary>
    /// Kolíky/skrutky z dotyku — len vonkajší korpus (nie šufle).
    /// CNC značenie (movento, podperky, …) sem nepatrí: má už presnú pozíciu z modelu.
    /// </summary>
    private static bool JeVonkajsiKorpusDotyk(ExportDocument doc, ContactMark c)
    {
        var a = Find(doc, c.PartA);
        var b = Find(doc, c.PartB);
        if (a == null || b == null) return false;
        if (a.JeSuflik || a.JeSuflikPozicia || b.JeSuflik || b.JeSuflikPozicia)
            return false;
        return true;
    }

    private sealed record ChrbatLayout(bool HeightOnX);

    /// <summary>
    /// Ktorý workpiece rozmer chrbta je výška skrinky (X vs Y) — podľa dĺžky boku.
    /// Odsadenie kolíkov rieši ResolveOdPredu, nie tento layout.
    /// </summary>
    private static bool TryGetChrbatLayout(ExportDocument doc, out ChrbatLayout layout)
    {
        layout = new ChrbatLayout(true);

        var chrbat = doc.Diely.FirstOrDefault(d =>
            !d.JeSuflik && RoleOf(doc, d) == "chrbat");
        var bok = doc.Diely.FirstOrDefault(d =>
            !d.JeSuflik && RoleOf(doc, d) is "bokL" or "bokP");
        if (chrbat == null || bok == null)
            return false;

        var dno = doc.Diely.FirstOrDefault(d =>
            !d.JeSuflik && RoleOf(doc, d) == "dno");
        var vrch = doc.Diely.FirstOrDefault(d =>
            !d.JeSuflik && RoleOf(doc, d) == "vrch");
        double dnoT = dno != null ? ThinDimension(dno) : 18;
        double vrchT = vrch != null ? ThinDimension(vrch) : 18;
        if (dnoT < 1) dnoT = 18;
        if (vrchT < 1) vrchT = 18;

        double bokLen = LongestNonThin(bok);
        var (dx, dy, _) = PartRules.For(chrbat).WorkpieceSize(chrbat);
        double withTh = bokLen + dnoT + vrchT;
        const double tol = 2.0;

        bool heightOnX;
        if (Math.Abs(dx - bokLen) <= tol || Math.Abs(dx - withTh) <= tol)
            heightOnX = true;
        else if (Math.Abs(dy - bokLen) <= tol || Math.Abs(dy - withTh) <= tol)
            heightOnX = false;
        else
        {
            double[] diffs =
            {
                Math.Abs(dx - bokLen), Math.Abs(dy - bokLen),
                Math.Abs(dx - withTh), Math.Abs(dy - withTh)
            };
            int best = 0;
            for (int i = 1; i < diffs.Length; i++)
                if (diffs[i] < diffs[best]) best = i;
            heightOnX = best is 0 or 2;
        }

        layout = new ChrbatLayout(heightOnX);
        return true;
    }

    private static double LongestNonThin(DielecModel d)
    {
        double thin = ThinDimension(d);
        double best = 0;
        foreach (double v in new[] { d.RozmerX, d.RozmerY, d.RozmerZ })
        {
            if (Math.Abs(v - thin) > 0.5 && v > best)
                best = v;
        }
        return best;
    }

    /// <summary>
    /// Chrbat ↔ bok. Bok: Back hrana, x=odPredu y=9, pattern (1,n,32,roztec), hĺbka 23.
    /// Chrbat: Top plocha — výška X: x=odPredu y=9 pattern (1,n,32,roztec);
    /// výška Y: x=9 y=odPredu pattern (n,1,roztec,32); hĺbka 12.
    /// 1. séria od dna, 2. (Z druhej strany) od vrchu.
    /// Bok L: Back RefPos 0 / 2 | Bok P: Back RefPos 2 / 0.
    /// Chrbat×L výška X: Top 1/3 | ×P: Top 0/2. Výška Y: Top L 0/1 | P 2/3.
    /// </summary>
    private static bool TryResolveChrbatBok(
        string selfRole, string partnerRole, bool odVrchu, ChrbatLayout layout,
        out DrillOrient orient, out bool isEdge)
    {
        orient = default;
        isEdge = true;

        if (selfRole is "bokL" or "bokP" && partnerRole == "chrbat")
        {
            // Back hrana; od dna / od vrchu — L: 0/2, P: 2/0
            int refPos = selfRole == "bokL"
                ? (odVrchu ? 2 : 0)
                : (odVrchu ? 0 : 2);
            orient = new DrillOrient("Back", refPos, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "chrbat" && partnerRole == "bokL")
        {
            if (layout.HeightOnX)
            {
                // Top plocha, pattern (1,n,32,roztec), x=odPredu y=9
                orient = new DrillOrient("Top", odVrchu ? 3 : 1, PatternAlongPrimary: false);
                return true;
            }

            // Top plocha, pattern (n,1,roztec,32), x=9 y=odPredu
            isEdge = false;
            orient = new DrillOrient("Top", odVrchu ? 1 : 0, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "chrbat" && partnerRole == "bokP")
        {
            if (layout.HeightOnX)
            {
                orient = new DrillOrient("Top", odVrchu ? 2 : 0, PatternAlongPrimary: false);
                return true;
            }

            isEdge = false;
            orient = new DrillOrient("Top", odVrchu ? 3 : 2, PatternAlongPrimary: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Dno/vrch ↔ chrbat režim A — dno/vrch=hrana (Back), chrbat=plocha (Top).
    /// 1. séria od boku L, 2. (Z druhej strany) od boku P.
    /// Zo stredu = stred pozdĺž šírky (OdPredu), ale na chrbate RefPos stále dno≠vrch
    /// (opačné hrany výšky — inak by padli na to isté miesto).
    /// Dno/vrch: Back, x=odPredu y=9, pattern (1,n,32,roztec).
    /// Chrbat výška na X → Top, x=9 y=odPredu, pattern (n,1,roztec,32).
    /// Chrbat výška na Y → Top, x=odPredu y=9, pattern (1,n,32,roztec).
    /// </summary>
    private static bool TryResolveDnoVrchChrbatModeA(
        string selfRole, string partnerRole, ChrbatLayout layout,
        bool zoStredu, bool odBokuP,
        out DrillOrient orient, out bool isEdge)
    {
        orient = default;
        isEdge = true;

        if ((selfRole is "dno" or "vrch") && partnerRole == "chrbat")
        {
            // Vrch: zrkadlo RefPos oproti dnu (L↔P). Zo stredu → RefPos 0 (samostatný dielec).
            int refPos = zoStredu
                ? 0
                : selfRole == "dno"
                    ? (odBokuP ? 0 : 2)
                    : (odBokuP ? 2 : 0);
            orient = new DrillOrient("Back", refPos, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole != "chrbat" || partnerRole is not ("dno" or "vrch"))
            return false;

        // Na chrbate: Zo stredu nemená hranu výšky — len OdPredu je zo stredu šírky.
        // HeightOnX: dno Ref 1 (L) / 0 (P), vrch Ref 3 (L) / 2 (P)
        // HeightOnY: dno Ref 1 (L) / 0 (P), vrch Ref 2 (L) / 3 (P)
        if (layout.HeightOnX)
        {
            isEdge = false;
            int chrbatRef = partnerRole == "dno"
                ? (zoStredu || !odBokuP ? 1 : 0)
                : (zoStredu || !odBokuP ? 3 : 2);
            orient = new DrillOrient("Top", chrbatRef, PatternAlongPrimary: true);
            return true;
        }

        isEdge = true;
        int chrbatRefY = partnerRole == "dno"
            ? (zoStredu || !odBokuP ? 1 : 0)
            : (zoStredu || !odBokuP ? 2 : 3);
        orient = new DrillOrient("Top", chrbatRefY, PatternAlongPrimary: false);
        return true;
    }

    /// <summary>
    /// Dno/vrch ↔ chrbat režim B — dno/vrch=plocha (Top), chrbat=hrana.
    /// Dno: Top RefPos 1 (stred/bok L) / 3 (bok P). Vrch: Top RefPos 3 (stred/bok L) / 1 (bok P). Pattern 1,n,32,roztec, x/y=odPredu/9.
    /// Chrbat×dno výška X: Left | ×vrch: Right — pattern (1,n,32,roztec).
    /// Chrbat×dno výška Y: Front | ×vrch: Back.
    /// </summary>
    private static bool TryResolveDnoVrchChrbatModeB(
        string selfRole, string partnerRole, ChrbatLayout layout,
        bool zoStredu, bool odBokuP,
        out DrillOrient orient, out bool isEdge)
    {
        orient = default;
        isEdge = true;

        if (selfRole == "dno" && partnerRole == "chrbat")
        {
            // Top, x=odPredu y=9, pattern (1,n,32,roztec); zo stredu / bok L → RefPos 1, bok P → 3
            isEdge = true;
            int refPos = odBokuP ? 3 : 1;
            orient = new DrillOrient("Top", refPos, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "vrch" && partnerRole == "chrbat")
        {
            // Top, x=odPredu y=9, pattern (1,n,32,roztec); zo stredu / bok L → RefPos 3, bok P → 1
            isEdge = true;
            int refPos = odBokuP ? 1 : 3;
            orient = new DrillOrient("Top", refPos, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole != "chrbat" || partnerRole is not ("dno" or "vrch"))
            return false;

        int edgeRef = zoStredu ? 0 : (odBokuP ? 0 : 2);
        if (layout.HeightOnX)
        {
            // Výška na X → dno/vrch = Left/Right hrany
            string wp = partnerRole == "dno" ? "Left" : "Right";
            orient = new DrillOrient(wp, edgeRef, PatternAlongPrimary: false);
            return true;
        }

        // Výška na Y → dno/vrch = Front/Back hrany
        string wpY = partnerRole == "dno" ? "Front" : "Back";
        orient = new DrillOrient(wpY, edgeRef, PatternAlongPrimary: false);
        return true;
    }

    /// <summary>
    /// Vrch ↔ bok režim A — vrch=hrana, bok=plocha.
    /// Vrch×bokL: Right/0 | Vrch×bokP: Left/2 — pattern (1,n,32,roztec), drill(30,9)
    /// BokL×vrch: Top/0 | BokP×vrch: Top/2 — pattern (n,1,roztec,32), drill(9, odPredu)
    /// </summary>
    private static bool TryResolveVrchBokModeA(string selfRole, string partnerRole, out DrillOrient orient)
    {
        orient = default;

        if (selfRole == "vrch" && partnerRole == "bokL")
        {
            orient = new DrillOrient("Right", 0, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "vrch" && partnerRole == "bokP")
        {
            orient = new DrillOrient("Left", 2, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "bokL" && partnerRole == "vrch")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "bokP" && partnerRole == "vrch")
        {
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Vrch ↔ bok režim B — vrch=plocha, bok=hrana.
    /// Vrch×bokL: Top/2 | Vrch×bokP: Top/0 — pattern (n,1,roztec,32), drill(9, odPredu)
    /// BokL×vrch: Left/2 | BokP×vrch: Right/0 — pattern (1,n,32,roztec), drill(30,9)
    /// </summary>
    private static bool TryResolveVrchBokModeB(string selfRole, string partnerRole, out DrillOrient orient)
    {
        orient = default;

        if (selfRole == "vrch" && partnerRole == "bokL")
        {
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "vrch" && partnerRole == "bokP")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "bokL" && partnerRole == "vrch")
        {
            orient = new DrillOrient("Left", 2, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "bokP" && partnerRole == "vrch")
        {
            orient = new DrillOrient("Right", 0, PatternAlongPrimary: false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Priecka ↔ bok: priecka vždy hrana, bok vždy plocha.
    /// Priecka×bokL: Left/2 | Priecka×bokP: Right/0 — X/Y rovnaké (30,9), pattern (1,n,32,roztec)
    /// BokL×priecka: Top/2 | BokP×priecka: Top/0 — X/Y rovnaké (ortho, odPredu), pattern (n,1,roztec,32)
    /// </summary>
    private static bool TryResolvePrieckaBok(string selfRole, string partnerRole, out DrillOrient orient)
    {
        orient = default;

        if (selfRole == "priecka" && partnerRole == "bokL")
        {
            orient = new DrillOrient("Left", 2, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "priecka" && partnerRole == "bokP")
        {
            orient = new DrillOrient("Right", 0, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "bokL" && partnerRole == "priecka")
        {
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "bokP" && partnerRole == "priecka")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Režim A — dno=plocha, bok=hrana (používateľský zápis).
    /// Dno×bokL: Top/0, pattern (n,1,roztec,32), drill(halfThin, odPredu)
    /// BokL×dno: Right/0, pattern (1,n,32,roztec), drill(odPredu, halfThin)
    /// Dno×bokP: Top/2, pattern (n,1,roztec,32), drill(halfThin, odPredu)
    /// BokP×dno: Left/2, pattern (1,n,32,roztec), drill(odPredu, halfThin)
    /// </summary>
    private static bool TryResolveDnoBokModeA(
        string selfRole, string partnerRole, DielecModel diel, ContactMark c, out DrillOrient orient)
    {
        orient = default;

        if (selfRole == "dno" && partnerRole == "bokL")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "dno" && partnerRole == "bokP")
        {
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "bokL" && partnerRole == "dno")
        {
            // Hrana pri dne: Right / Ref 0 (kontakt pri Min Z boku)
            bool nearMax = PartRules.ContactNearMax(c, diel);
            orient = nearMax
                ? new DrillOrient("Left", 2, PatternAlongPrimary: false)
                : new DrillOrient("Right", 0, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "bokP" && partnerRole == "dno")
        {
            bool nearMax = PartRules.ContactNearMax(c, diel);
            orient = nearMax
                ? new DrillOrient("Right", 0, PatternAlongPrimary: false)
                : new DrillOrient("Left", 2, PatternAlongPrimary: false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Režim B — dno=hrana, bok=plocha (dno medzi bokmi).
    /// Dno×bokL: Left/2, pattern (1,n,32,roztec), drill(odPredu, halfThin)
    /// BokL×dno: Top/2, pattern (n,1,roztec,32), drill(ortho, odPredu)
    /// BokP / Dno×bokP: zrkadlo (Right/0, Top/0).
    /// </summary>
    private static bool TryResolveDnoBokModeB(string selfRole, string partnerRole, out DrillOrient orient)
    {
        orient = default;

        if (selfRole == "dno" && partnerRole == "bokL")
        {
            orient = new DrillOrient("Left", 2, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "dno" && partnerRole == "bokP")
        {
            orient = new DrillOrient("Right", 0, PatternAlongPrimary: false);
            return true;
        }

        if (selfRole == "bokL" && partnerRole == "dno")
        {
            orient = new DrillOrient("Top", 2, PatternAlongPrimary: true);
            return true;
        }

        if (selfRole == "bokP" && partnerRole == "dno")
        {
            orient = new DrillOrient("Top", 0, PatternAlongPrimary: true);
            return true;
        }

        return false;
    }

    private static void WriteDrillBlock(StringBuilder sb, DielecModel diel, DrillOp op)
    {
        double halfThin = ThinDimension(diel) * 0.5;
        double x, y;
        if (op.IsEdge)
        {
            x = op.OdPredu;
            y = op.OrthoPos ?? halfThin;
        }
        else if (op.OrthoPos is double ortho)
        {
            x = ortho;
            y = op.OdPredu;
        }
        else
        {
            x = halfThin;
            y = op.OdPredu;
        }

        string wp = RemapWorkplane(diel, op.Workplane);
        if (string.IsNullOrWhiteSpace(wp))
            wp = op.IsEdge ? "Back" : "Top";
        int refPos = op.RefPos;
        if (string.Equals(wp, "Left", StringComparison.OrdinalIgnoreCase)) refPos = 2;
        else if (string.Equals(wp, "Right", StringComparison.OrdinalIgnoreCase)) refPos = 0;

        sb.AppendLine($"SelectWorkplane(\"{wp}\");");
        sb.AppendLine($"SetReferencePosition({refPos});");

        if (op.Pocet > 1 && op.Roztec > 0)
        {
            // Plocha (Top/Bottom): vždy (n,1,roztec,32) ako kolíky — aj keď by op mal edge flag.
            bool alongPrimary = op.PatternAlongPrimary
                || (!op.IsEdge && IsTopOrBottomWorkplane(wp));
            if (alongPrimary)
                sb.AppendLine($"CreatePattern({op.Pocet},1,{Fmt(op.Roztec)},{Fmt(PatternStepMm)},0,90);");
            else
                sb.AppendLine($"CreatePattern(1,{op.Pocet},{Fmt(PatternStepMm)},{Fmt(op.Roztec)},0,90);");
        }

        sb.AppendLine(
            $"CreateDrill(\"{Escape(op.Id)}\",{Fmt(x)},{Fmt(y)},{Fmt(op.Depth)},{Fmt(op.Dia)}," +
            $"\"{Escape(op.Description)}\",TypeOfProcess.Drilling,\"{op.Tool}\",\"-1\",3,-1,-1,\"-1\");");

        if (op.Pocet > 1 && op.Roztec > 0)
            sb.AppendLine("ResetPattern();");

        sb.AppendLine();
    }

    private static bool IsTopOrBottomWorkplane(string wp) =>
        string.Equals(wp, "Top", StringComparison.OrdinalIgnoreCase)
        || string.Equals(wp, "Bottom", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Movento z Excel CNC značenia — absolútne súradnice z modelu, bez odsadenia.
    /// Predznačenie Ø3 × 3.5; X = výška, Y = od prednej hrany, CreatePattern(2,1,224,32,0,90).
    /// </summary>
    private static List<AbsDrillOp> CollectMoventoOps(ExportDocument doc, DielecModel diel)
    {
        var ops = new List<AbsDrillOp>();
        int refPos = TopRefPosForPart(doc, diel);

        var pts = diel.CncZnacenia
            .Where(z => string.IsNullOrEmpty(z.Handle)
                        || !z.Handle.StartsWith(DrillGenerator.GeneratedHandlePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(CncZnacenieTyp.IsMovento)
            .Select(z =>
            {
                FaceToWorkpieceXY(diel, z.PosX, z.PosY, out double x, out double y);
                double dia = z.Priemer > 0.1 ? z.Priemer : 3.0;
                return (X: x, Y: y, Dia: dia);
            })
            .ToList();

        if (pts.Count == 0)
            return ops;

        // FaceToWorkpiece Y=0 = predok (ako výrezy) — bez flipu dy−Y.
        var cols = pts
            .GroupBy(p => Math.Round(p.X * 2) / 2.0)
            .OrderBy(g => g.Key)
            .ToList();

        int idx = 1;
        foreach (var col in cols)
        {
            var front = col.OrderBy(p => p.Y).First(); // najbližšie k predku

            string id = idx == 1 ? "movento" : $"movento_{idx}";
            ops.Add(new AbsDrillOp(
                Workplane: "Top",
                RefPos: refPos,
                Id: id,
                Description: id,
                X: front.X,
                Y: front.Y,
                Depth: PredznacenieHlbkaMm,
                Dia: front.Dia > 0.1 ? front.Dia : 3.0,
                PatternCountX: 2,
                PatternCountY: 1,
                PitchX: MoventoPatternPitchMm,
                PitchY: PatternStepMm));
            idx++;
        }

        return ops;
    }

    /// <summary>
    /// Podperky z Excel CNC značenia — absolútne súradnice z modelu, bez odsadenia.
    /// X = výška, Y = od prednej hrany (nie od zadu).
    /// CreatePattern(po_hĺbke, po_výške, rozteč_hĺbka, rozteč_výška, 0, 90).
    /// </summary>
    private static List<AbsDrillOp> CollectPodperkyOps(ExportDocument doc, DielecModel diel)
    {
        var ops = new List<AbsDrillOp>();
        int refPos = TopRefPosForPart(doc, diel);

        var pts = diel.CncZnacenia
            .Where(z => string.IsNullOrEmpty(z.Handle)
                        || !z.Handle.StartsWith(DrillGenerator.GeneratedHandlePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(CncZnacenieTyp.IsPodperky)
            .Select(z =>
            {
                FaceToWorkpieceXY(diel, z.PosX, z.PosY, out double x, out double y);
                double dia = z.Priemer > 0.1 ? z.Priemer : 3.0;
                return (X: x, Y: y, Dia: dia);
            })
            .ToList();

        if (pts.Count == 0)
            return ops;

        // FaceToWorkpiece Y=0 = predok — bez flipu (rovnako ako výrezy / závesy).
        var shelfGroups = ClusterPodperkyShelves(pts);
        int idx = 1;
        foreach (var shelf in shelfGroups)
        {
            if (shelf.Count == 0) continue;

            double x0 = shelf.Min(p => p.X);
            double y0 = shelf.Min(p => p.Y); // najbližšie k predku
            double dia = shelf[0].Dia;
            string id = idx == 1 ? "podperky" : $"podperky_{idx}";

            var xs = shelf.Select(p => p.X).Distinct().OrderBy(v => v).ToList(); // výška
            var ys = shelf.Select(p => p.Y).Distinct().OrderBy(v => v).ToList(); // hĺbka od predku
            int countHlbka = Math.Max(1, ys.Count);
            int countVyska = Math.Max(1, xs.Count);
            double pitchHlbka = countHlbka >= 2 ? ys[1] - ys[0] : PodperkyPatternPitchHlbka;
            double pitchVyska = countVyska >= 2 ? xs[1] - xs[0] : PodperkyPatternPitchVyska;
            if (pitchHlbka < 1) pitchHlbka = PodperkyPatternPitchHlbka;
            if (pitchVyska < 1) pitchVyska = PodperkyPatternPitchVyska;

            ops.Add(new AbsDrillOp(
                Workplane: "Top",
                RefPos: refPos,
                Id: id,
                Description: id,
                X: x0,
                Y: y0,
                Depth: PodperkyHlbkaMm,
                Dia: dia > 0.1 ? dia : 3.0,
                PatternCountX: countHlbka,
                PatternCountY: countVyska,
                PitchX: pitchHlbka,
                PitchY: pitchVyska));
            idx++;
        }

        return ops;
    }

    /// <summary>
    /// Nohy (znacenie CNC_nohy) — Bottom, 2. upnutie po ABS (program B).
    /// Typicky 4 nohy × 4 diery → CreatePattern 2×2. Ø/hĺbka z Excelu.
    /// </summary>
    private static List<AbsDrillOp> CollectNohyOps(ExportDocument doc, DielecModel diel)
    {
        var ops = new List<AbsDrillOp>();
        int refPos = TopRefPosForPart(doc, diel);

        var pts = diel.CncZnacenia
            .Where(z => string.IsNullOrEmpty(z.Handle)
                        || !z.Handle.StartsWith(DrillGenerator.GeneratedHandlePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(CncZnacenieTyp.IsNohy)
            .Select(z =>
            {
                FaceToWorkpieceXY(diel, z.PosX, z.PosY, out double x, out double y);
                double dia = z.Priemer > 0.1 ? z.Priemer : 3.0;
                double depth = z.Hlbka > 0.5 ? z.Hlbka : PodperkyHlbkaMm;
                return (X: x, Y: y, Dia: dia, Depth: depth);
            })
            .ToList();

        if (pts.Count == 0)
            return ops;

        var clusters = ClusterNohyFeet(pts);
        int idx = 1;
        foreach (var cluster in clusters)
        {
            string id = idx == 1 ? "nohy" : $"nohy_{idx}";
            var xs = cluster.Select(p => p.X).Distinct().OrderBy(v => v).ToList();
            var ys = cluster.Select(p => p.Y).Distinct().OrderBy(v => v).ToList();
            double dia = cluster[0].Dia > 0.1 ? cluster[0].Dia : 3.0;
            double depth = cluster[0].Depth > 0.5 ? cluster[0].Depth : PodperkyHlbkaMm;

            if (xs.Count == 2 && ys.Count == 2 && cluster.Count == 4)
            {
                ops.Add(new AbsDrillOp(
                    Workplane: "Bottom",
                    RefPos: refPos,
                    Id: id,
                    Description: id,
                    X: xs[0],
                    Y: ys[0],
                    Depth: depth,
                    Dia: dia,
                    PatternCountX: 2,
                    PatternCountY: 2,
                    PitchX: xs[1] - xs[0],
                    PitchY: ys[1] - ys[0]));
            }
            else
            {
                int sub = 1;
                foreach (var p in cluster.OrderBy(p => p.X).ThenBy(p => p.Y))
                {
                    string sid = cluster.Count == 1 && clusters.Count == 1
                        ? id
                        : $"{id}_{sub}";
                    ops.Add(new AbsDrillOp(
                        Workplane: "Bottom",
                        RefPos: refPos,
                        Id: sid,
                        Description: sid,
                        X: p.X,
                        Y: p.Y,
                        Depth: p.Depth > 0.5 ? p.Depth : depth,
                        Dia: p.Dia > 0.1 ? p.Dia : dia));
                    sub++;
                }
            }

            idx++;
        }

        return ops;
    }

    /// <summary>Zhluky nôh — medzera medzi nohami typicky ≫ rozteč 4 dier (~64).</summary>
    private const double NohyClusterGapMm = 100.0;

    private static List<List<(double X, double Y, double Dia, double Depth)>> ClusterNohyFeet(
        List<(double X, double Y, double Dia, double Depth)> pts)
    {
        var remaining = pts.ToList();
        var clusters = new List<List<(double X, double Y, double Dia, double Depth)>>();

        while (remaining.Count > 0)
        {
            var seed = remaining[0];
            remaining.RemoveAt(0);
            var cluster = new List<(double X, double Y, double Dia, double Depth)> { seed };
            bool grew;
            do
            {
                grew = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var p = remaining[i];
                    bool near = cluster.Any(c =>
                    {
                        double dx = p.X - c.X, dy = p.Y - c.Y;
                        return Math.Sqrt(dx * dx + dy * dy) <= NohyClusterGapMm;
                    });
                    if (!near) continue;
                    cluster.Add(p);
                    remaining.RemoveAt(i);
                    grew = true;
                }
            } while (grew);

            clusters.Add(cluster);
        }

        return clusters
            .OrderBy(c => c.Average(p => p.X))
            .ThenBy(c => c.Average(p => p.Y))
            .ToList();
    }

    /// <summary>
    /// Závesy — len plošné diery (predznačenie Ø3 × 3.5). Hranové (do dvierok) sa vynechajú.
    /// X = výška, Y = od predku (FaceToWorkpiece, bez flipu); CreatePattern(2,1,32,…).
    /// Klasický typ (vrstva _zaves / Typ zaves) — bez zaves_rady.
    /// </summary>
    private static List<AbsDrillOp> CollectZavesOps(ExportDocument doc, DielecModel diel)
    {
        var ops = new List<AbsDrillOp>();
        int refPos = TopRefPosForPart(doc, diel);

        var pts = diel.CncZnacenia
            .Where(z => string.IsNullOrEmpty(z.Handle)
                        || !z.Handle.StartsWith(DrillGenerator.GeneratedHandlePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(CncZnacenieTyp.IsZaves)
            .Where(z => !CncZnacenieTyp.IsZavesRady(z))
            .Where(z => !PartRules.IsOutsideAabbFace(diel, z.PosX, z.PosY))
            .Select(z =>
            {
                FaceToWorkpieceXY(diel, z.PosX, z.PosY, out double x, out double y);
                double dia = z.Priemer > 0.1 ? z.Priemer : 3.0;
                return (X: x, Y: y, Dia: dia);
            })
            .ToList();

        if (pts.Count == 0)
            return ops;

        // FaceToWorkpiece Y=0 = predok — bez flipu dy−Y (ten dával závesy na zadok).
        var cols = pts
            .GroupBy(p => Math.Round(p.X * 2) / 2.0)
            .OrderBy(g => g.Key)
            .ToList();

        int idx = 1;
        foreach (var col in cols)
        {
            var ordered = col.OrderBy(p => p.Y).ToList(); // od predku
            var first = ordered[0];
            string id = idx == 1 ? "zavesy" : $"zavesy_{idx}";

            int countHlbka = ordered.Count;
            double pitchHlbka = ZavesPatternPitchMm;
            if (countHlbka >= 2)
            {
                pitchHlbka = ordered[1].Y - ordered[0].Y;
                if (pitchHlbka < 1) pitchHlbka = ZavesPatternPitchMm;
            }
            else
            {
                countHlbka = 1;
            }

            ops.Add(new AbsDrillOp(
                Workplane: "Top",
                RefPos: refPos,
                Id: id,
                Description: id,
                X: first.X,
                Y: first.Y,
                Depth: PredznacenieHlbkaMm,
                Dia: first.Dia > 0.1 ? first.Dia : 3.0,
                PatternCountX: countHlbka,
                PatternCountY: 1,
                PitchX: pitchHlbka,
                PitchY: PatternStepMm));
            idx++;
        }

        return ops;
    }

    /// <summary>
    /// Závesy v rade (System32): pár dier ~32 mm na jednej osi, viac závesov pozdĺž druhej.
    /// Id: zavesy_rady / zavesy_rady_2… Predznačenie Ø3 × 3.5. Klasické CollectZavesOps ostáva.
    /// </summary>
    private static List<AbsDrillOp> CollectZavesRadyOps(ExportDocument doc, DielecModel diel)
    {
        var ops = new List<AbsDrillOp>();
        int refPos = TopRefPosForPart(doc, diel);

        var pts = diel.CncZnacenia
            .Where(z => string.IsNullOrEmpty(z.Handle)
                        || !z.Handle.StartsWith(DrillGenerator.GeneratedHandlePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(CncZnacenieTyp.IsZavesRady)
            .Where(z => !PartRules.IsOutsideAabbFace(diel, z.PosX, z.PosY))
            .Select(z =>
            {
                FaceToWorkpieceXY(diel, z.PosX, z.PosY, out double x, out double y);
                double dia = z.Priemer > 0.1 ? z.Priemer : 3.0;
                return (X: x, Y: y, Dia: dia);
            })
            .ToList();

        if (pts.Count == 0)
            return ops;

        static List<double> DistinctRounded(IEnumerable<double> vals) =>
            vals.Select(v => Math.Round(v * 2.0) / 2.0).Distinct().OrderBy(v => v).ToList();

        var xs = DistinctRounded(pts.Select(p => p.X));
        var ys = DistinctRounded(pts.Select(p => p.Y));
        bool Pair32(List<double> a) =>
            a.Count == 2 && Math.Abs(a[1] - a[0] - ZavesPatternPitchMm) <= 2.5;

        bool pairOnX = Pair32(xs);
        bool pairOnY = Pair32(ys);
        if (!pairOnX && !pairOnY)
            return ops;

        int idx = 1;
        if (pairOnX)
        {
            // Pár na X (32 mm), rady pozdĺž Y (výška / od predku podľa orientácie).
            foreach (var row in pts.GroupBy(p => Math.Round(p.Y * 2) / 2.0).OrderBy(g => g.Key))
            {
                var ordered = row.OrderBy(p => p.X).ToList();
                var first = ordered[0];
                string id = idx == 1 ? "zavesy_rady" : $"zavesy_rady_{idx}";
                double pitch = ordered.Count >= 2
                    ? ordered[1].X - ordered[0].X
                    : ZavesPatternPitchMm;
                if (pitch < 1) pitch = ZavesPatternPitchMm;

                ops.Add(new AbsDrillOp(
                    Workplane: "Top",
                    RefPos: refPos,
                    Id: id,
                    Description: id,
                    X: first.X,
                    Y: first.Y,
                    Depth: PredznacenieHlbkaMm,
                    Dia: first.Dia > 0.1 ? first.Dia : 3.0,
                    PatternCountX: Math.Max(1, ordered.Count),
                    PatternCountY: 1,
                    PitchX: pitch,
                    PitchY: PatternStepMm));
                idx++;
            }
        }
        else
        {
            // Pár na Y (32 mm), rady pozdĺž X.
            foreach (var col in pts.GroupBy(p => Math.Round(p.X * 2) / 2.0).OrderBy(g => g.Key))
            {
                var ordered = col.OrderBy(p => p.Y).ToList();
                var first = ordered[0];
                string id = idx == 1 ? "zavesy_rady" : $"zavesy_rady_{idx}";
                double pitch = ordered.Count >= 2
                    ? ordered[1].Y - ordered[0].Y
                    : ZavesPatternPitchMm;
                if (pitch < 1) pitch = ZavesPatternPitchMm;

                ops.Add(new AbsDrillOp(
                    Workplane: "Top",
                    RefPos: refPos,
                    Id: id,
                    Description: id,
                    X: first.X,
                    Y: first.Y,
                    Depth: PredznacenieHlbkaMm,
                    Dia: first.Dia > 0.1 ? first.Dia : 3.0,
                    PatternCountX: 1,
                    PatternCountY: Math.Max(1, ordered.Count),
                    PitchX: PatternStepMm,
                    PitchY: pitch));
                idx++;
            }
        }

        return ops;
    }

    /// <summary>Rozdelí body podperiek podľa výšky (skupiny políc v smere X).</summary>
    private static List<List<(double X, double Y, double Dia)>> ClusterPodperkyShelves(
        List<(double X, double Y, double Dia)> pts)
    {
        var xs = pts.Select(p => p.X).Distinct().OrderBy(v => v).ToList();
        var shelfXRanges = new List<(double Min, double Max)>();
        if (xs.Count > 0)
        {
            double min = xs[0], max = xs[0];
            for (int i = 1; i < xs.Count; i++)
            {
                if (xs[i] - xs[i - 1] > PodperkyShelfGapMm)
                {
                    shelfXRanges.Add((min, max));
                    min = xs[i];
                }
                max = xs[i];
            }
            shelfXRanges.Add((min, max));
        }

        var result = new List<List<(double X, double Y, double Dia)>>();
        foreach (var (minX, maxX) in shelfXRanges)
        {
            var shelf = pts
                .Where(p => p.X >= minX - 0.1 && p.X <= maxX + 0.1)
                .ToList();
            if (shelf.Count > 0)
                result.Add(shelf);
        }
        return result;
    }

    /// <summary>
    /// Len geometria výrezov / zafrezov (CreatePolyline…) — bez frezovacej stratégie.
    /// Súradnice z VyrezProfileBuilder už majú Y=0 = predok (ako Top RefPos),
    /// preto sa Y NEFLIPUJE (na rozdiel od CNC značenia movento/podperky).
    /// Bok L/P: L bez zmeny smeru, U opačný smer.
    /// </summary>
    private static void WriteVyrezGeometry(StringBuilder sb, ExportDocument doc, DielecModel diel)
    {
        var profile = VyrezProfileBuilder.Build(diel);
        if (profile.BorderCutouts.Count == 0 && profile.Holes.Count == 0)
            return;

        var (dx, dy, _) = PartRules.For(diel).WorkpieceSize(diel);
        string role = RoleOf(doc, diel);

        int zafrezN = 1;
        int refPos = TopRefPosForPart(doc, diel);
        string wp = RemapWorkplane(diel, "Top");
        foreach (var cut in profile.BorderCutouts)
        {
            sb.AppendLine($"SelectWorkplane(\"{wp}\");");
            sb.AppendLine($"SetReferencePosition({refPos});");
            var path = OpenBorderCutPath(cut, dx, dy, out bool isL);
            bool reverse = !isL && (role == "bokL" || role == "bokP");
            if (reverse) path.Reverse();
            WritePolylineGeometry(sb, $"geo_Zafrez{zafrezN}", path, close: false);
            zafrezN++;
        }

        int vyrezN = 1;
        foreach (var hole in profile.Holes)
        {
            sb.AppendLine($"SelectWorkplane(\"{wp}\");");
            sb.AppendLine($"SetReferencePosition({refPos});");
            var path = VyrezProfileBuilder.InteriorEntryPath(hole);
            if (role == "bokL") path.Reverse();
            WritePolylineGeometry(sb, $"geo_Vyrez{vyrezN}", path, close: true);
            vyrezN++;
        }
    }

    /// <summary>
    /// Otvorený zafrez: L = 3 body na susedných hranách; U = 4 body na jednej hrane.
    /// </summary>
    private static List<(double X, double Y)> OpenBorderCutPath(
        List<(double X, double Y)> cut, double dx, double dy, out bool isL)
    {
        isL = false;
        if (cut.Count < 3) return cut;

        var pts = cut
            .Where(p => !IsAabbCorner(p, dx, dy))
            .ToList();
        if (pts.Count < 3) pts = cut.ToList();

        int b0 = BorderId(pts[0], dx, dy);
        int b1 = BorderId(pts[^1], dx, dy);
        if (b0 < 0) b0 = GuessBorder(pts[0], dx, dy);
        if (b1 < 0) b1 = GuessBorder(pts[^1], dx, dy);

        if (b0 >= 0 && b1 >= 0 && b0 != b1
            && (Math.Abs(b0 - b1) == 1 || (b0 == 0 && b1 == 3) || (b0 == 3 && b1 == 0)))
        {
            isL = true;
            return DedupNearPts(pts);
        }

        return OpenUPath(pts);
    }

    private static bool IsAabbCorner((double X, double Y) p, double dx, double dy)
    {
        bool onX = Math.Abs(p.X) < 0.2 || Math.Abs(p.X - dx) < 0.2;
        bool onY = Math.Abs(p.Y) < 0.2 || Math.Abs(p.Y - dy) < 0.2;
        return onX && onY;
    }

    private static int BorderId((double X, double Y) p, double dx, double dy)
    {
        bool b = Math.Abs(p.Y) < 0.2;
        bool r = Math.Abs(p.X - dx) < 0.2;
        bool t = Math.Abs(p.Y - dy) < 0.2;
        bool l = Math.Abs(p.X) < 0.2;
        int c = (b ? 1 : 0) + (r ? 1 : 0) + (t ? 1 : 0) + (l ? 1 : 0);
        if (c != 1) return -1;
        if (b) return 0;
        if (r) return 1;
        if (t) return 2;
        return 3;
    }

    private static int GuessBorder((double X, double Y) p, double dx, double dy)
    {
        double db = Math.Abs(p.Y);
        double dr = Math.Abs(p.X - dx);
        double dt = Math.Abs(p.Y - dy);
        double dl = Math.Abs(p.X);
        double m = Math.Min(Math.Min(db, dr), Math.Min(dt, dl));
        if (Math.Abs(m - db) < 0.01) return 0;
        if (Math.Abs(m - dr) < 0.01) return 1;
        if (Math.Abs(m - dt) < 0.01) return 2;
        return 3;
    }

    private static List<(double X, double Y)> DedupNearPts(List<(double X, double Y)> pts)
    {
        var r = new List<(double X, double Y)>();
        foreach (var p in pts)
        {
            if (r.Count > 0
                && Math.Abs(r[^1].X - p.X) < 0.2
                && Math.Abs(r[^1].Y - p.Y) < 0.2)
                continue;
            r.Add(p);
        }
        return r;
    }

    private static List<(double X, double Y)> OpenUPath(List<(double X, double Y)> cut)
    {
        if (cut.Count < 3) return cut;

        double minX = cut.Min(p => p.X), maxX = cut.Max(p => p.X);
        double minY = cut.Min(p => p.Y), maxY = cut.Max(p => p.Y);
        bool wide = (maxX - minX) >= (maxY - minY);

        if (wide)
        {
            int onMinY = cut.Count(p => Math.Abs(p.Y - minY) < 0.2);
            int onMaxY = cut.Count(p => Math.Abs(p.Y - maxY) < 0.2);
            if (onMinY >= 2 && onMinY >= onMaxY)
            {
                return new List<(double X, double Y)>
                {
                    (minX, minY), (minX, maxY), (maxX, maxY), (maxX, minY)
                };
            }
            return new List<(double X, double Y)>
            {
                (minX, maxY), (minX, minY), (maxX, minY), (maxX, maxY)
            };
        }

        int onMinX = cut.Count(p => Math.Abs(p.X - minX) < 0.2);
        int onMaxX = cut.Count(p => Math.Abs(p.X - maxX) < 0.2);
        if (onMinX >= 2 && onMinX >= onMaxX)
        {
            return new List<(double X, double Y)>
            {
                (minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY)
            };
        }
        return new List<(double X, double Y)>
        {
            (maxX, minY), (minX, minY), (minX, maxY), (maxX, maxY)
        };
    }

    private static void WritePolylineGeometry(
        StringBuilder sb, string name, List<(double X, double Y)> path, bool close)
    {
        if (path.Count < 2) return;

        sb.AppendLine($"CreatePolyline(\"{Escape(name)}\",{Fmt(path[0].X)},{Fmt(path[0].Y)});");
        for (int i = 1; i < path.Count; i++)
            sb.AppendLine($"AddSegmentToPolyline({Fmt(path[i].X)},{Fmt(path[i].Y)});");
        if (close)
            sb.AppendLine($"ClosePolyline(\"{Escape(name)}\");");
        sb.AppendLine();
    }

    private static void WriteAbsoluteDrill(
        StringBuilder sb, DielecModel diel, AbsDrillOp op, UpnutieVariant upnutie = UpnutieVariant.Single)
    {
        sb.AppendLine($"SelectWorkplane(\"{RemapWorkplane(diel, op.Workplane, upnutie)}\");");
        sb.AppendLine($"SetReferencePosition({op.RefPos});");

        bool patterned = op.PatternCountX > 1 || op.PatternCountY > 1;
        if (patterned)
            sb.AppendLine(
                $"CreatePattern({op.PatternCountX},{op.PatternCountY}," +
                $"{Fmt(op.PitchX)},{Fmt(op.PitchY)},0,90);");

        sb.AppendLine(
            $"CreateDrill(\"{Escape(op.Id)}\",{Fmt(op.X)},{Fmt(op.Y)},{Fmt(op.Depth)},{Fmt(op.Dia)}," +
            $"\"{Escape(op.Description)}\",TypeOfProcess.Drilling,\"-1\",\"-1\",3,-1,-1,\"-1\");");

        if (patterned)
            sb.AppendLine("ResetPattern();");

        sb.AppendLine();
    }

    private static int TopRefPosForPart(ExportDocument doc, DielecModel diel) =>
        RoleOf(doc, diel) switch
        {
            "bokL" => 2,
            "bokP" => 0,
            _ => 0
        };

    /// <summary>
    /// Excel Pos X/Y (osy plochy v poradí AABB) → X/Y workpiece (rovnako ako WorkpieceSize).
    /// </summary>
    private static void FaceToWorkpieceXY(DielecModel diel, double faceX, double faceY, out double wpX, out double wpY)
    {
        if (PartRules.WorkpieceSwapsFaceAxes(diel))
        {
            wpX = faceY;
            wpY = faceX;
        }
        else
        {
            wpX = faceX;
            wpY = faceY;
        }
    }

    private static int ThinAxis(DielecModel d)
    {
        int thin = 0;
        if (d.RozmerY < d.RozmerX) thin = 1;
        if (d.RozmerZ < (thin == 0 ? d.RozmerX : d.RozmerY)) thin = 2;
        return thin;
    }

    private static string RemapWorkplane(
        DielecModel diel, string workplane, UpnutieVariant upnutie = UpnutieVariant.Single)
    {
        // B = dielec otočený spodkom hore → Bottom sa vŕta ako Top.
        bool flip = diel.OtocitSpodkomHore || upnutie == UpnutieVariant.B;
        if (!flip)
            return workplane;
        return workplane switch
        {
            "Bottom" => "Top",
            "Top" => "Bottom",
            "Left" => "Right",
            "Right" => "Left",
            _ => workplane
        };
    }

    private static int ApplyZDruhejRef(int baseRef, bool zDruhej)
        => !zDruhej ? baseRef : (baseRef == 2 ? 0 : 2);

    /// <summary>
    /// Od predu v XCS.
    /// Bežne: UI + odsadenie styku od 0/max.
    /// Zo stredu: stred TOHTO dielca − ((n−1)/2)·rozteč (nie dĺžka dotyku).
    /// Len kolíky/skrutky na vonkajšom korpuse. CNC značenie sa neposúva.
    /// </summary>
    private static double ResolveOdPredu(DielecModel diel, ContactMark contact, KolikSerie serie)
    {
        if (serie.ZoStredu)
        {
            double len = PartLengthAlongContactPrimary(diel, contact);
            double odPredu = len * 0.5 - (serie.PocetKolikov - 1) * 0.5 * serie.RoztecKolikov;
            if (odPredu < 0) odPredu = 0;
            return odPredu;
        }

        return AdjustOdPreduForPart(diel, contact, serie.OdPredu, serie.ZDruhejStrany);
    }

    private static double PartLengthAlongContactPrimary(DielecModel diel, ContactMark contact)
    {
        SkrutkaLayout.GetPrimaryAxes(contact, out int axis, out _);
        return axis switch
        {
            0 => diel.RozmerX,
            1 => diel.RozmerY,
            _ => diel.RozmerZ
        };
    }

    /// <summary>
    /// Odsadenie styčnej plochy od min (alebo od max pri z druhej strany).
    /// </summary>
    private static double AdjustOdPreduForPart(
        DielecModel diel, ContactMark contact, double odPredu, bool zDruhej)
    {
        SkrutkaLayout.GetPrimaryAxes(contact, out int measureAxis, out _);

        double partMin = measureAxis switch
        {
            0 => diel.WcsMinX,
            1 => diel.WcsMinY,
            _ => diel.WcsMinZ
        };
        double partLen = measureAxis switch
        {
            0 => diel.RozmerX,
            1 => diel.RozmerY,
            _ => diel.RozmerZ
        };
        double partMax = partMin + partLen;

        double[] cSize = { contact.Size.X, contact.Size.Y, contact.Size.Z };
        double[] cCenter = { contact.Center.X, contact.Center.Y, contact.Center.Z };
        double contactMin = cCenter[measureAxis] - cSize[measureAxis] * 0.5;
        double contactMax = cCenter[measureAxis] + cSize[measureAxis] * 0.5;

        double inset = zDruhej
            ? partMax - contactMax
            : contactMin - partMin;
        if (inset < 1.0)
            inset = 0;

        return odPredu + inset;
    }

    private static bool TryContactOrthoOnWorkpiece(DielecModel diel, ContactMark c, out double ortho)
    {
        ortho = 0;
        SkrutkaLayout.GetPrimaryAxes(c, out _, out int secondary);
        double partMin = secondary switch
        {
            0 => diel.WcsMinX,
            1 => diel.WcsMinY,
            _ => diel.WcsMinZ
        };
        double center = secondary switch
        {
            0 => c.Center.X,
            1 => c.Center.Y,
            _ => c.Center.Z
        };
        ortho = center - partMin;
        return ortho >= -1 && ortho <= 1e6;
    }

    private static void ResolvePlocha(
        ContactMark c, DielecModel? partA, DielecModel? partB,
        out bool aPlocha, out bool bPlocha, out DielecModel? plocha)
    {
        aPlocha = IsPlocha(partA, c.Axis);
        bPlocha = IsPlocha(partB, c.Axis);
        if (aPlocha == bPlocha)
        {
            aPlocha = false;
            bPlocha = true;
        }
        plocha = bPlocha ? partB : partA;
    }

    private static bool IsPlocha(DielecModel? d, int contactAxis)
    {
        if (d == null) return false;
        int thin = 0;
        if (d.RozmerY < d.RozmerX) thin = 1;
        if (d.RozmerZ < (thin == 0 ? d.RozmerX : d.RozmerY)) thin = 2;
        return thin == contactAxis;
    }

    private static double ThinDimension(DielecModel d) => PartRules.ThinDimension(d);

    private static DielecModel? Find(ExportDocument doc, string name) =>
        doc.Diely.FirstOrDefault(d => string.Equals(d.Nazov, name, StringComparison.OrdinalIgnoreCase));

    private static string RoleOf(ExportDocument doc, DielecModel diel)
        => PartRules.DetectRole(diel, doc);

    private static string RoleOf(ExportDocument doc, string nazov)
    {
        var d = Find(doc, nazov);
        return d != null ? PartRules.DetectRole(d, doc) : PartRules.DetectRole(nazov);
    }

    private static string ContactOpName(ExportDocument doc, string kindPrefix, string partnerNazov, int serieN)
    {
        string name = $"{kindPrefix}_{PartnerOpLabel(doc, partnerNazov)}";
        if (serieN > 1)
            name = $"{name}_{serieN}";
        return RemoveDiacritics(name);
    }

    private static string PartnerOpLabel(ExportDocument doc, string nazov) =>
        RoleOf(doc, nazov) switch
        {
            "dno" => "dno",
            "vrch" => "vrch",
            "bokL" => "bok L",
            "bokP" => "bok P",
            "priecka" => "priecka",
            _ => RemoveDiacritics(Regex.Replace(nazov.Trim(), @"^\d+_", ""))
                .Replace('_', ' ').Trim().ToLowerInvariant()
        };

    private static void EnsureUniqueNames(List<DrillOp> ops)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ops.Count; i++)
        {
            string id = ops[i].Id;
            if (used.Add(id))
                continue;
            int n = 2;
            string candidate;
            do { candidate = $"{id}_{n++}"; }
            while (!used.Add(candidate));
            ops[i] = ops[i] with { Id = candidate, Description = candidate };
        }
    }

    private static string WorkpieceDisplayName(string nazov)
    {
        string s = Regex.Replace(nazov.Trim(), @"^\d+_", "");
        s = Regex.Replace(s, @"\s+([LPlp])\s*$", "$1");
        return RemoveDiacritics(s);
    }

    private static string SafeFileName(string nazov)
    {
        nazov = RemoveDiacritics(nazov);
        foreach (char ch in Path.GetInvalidFileNameChars())
            nazov = nazov.Replace(ch, '_');
        return nazov.Trim();
    }

    private static string Fmt(double v) => v.ToString("0.00", I);
    private static string MacroStr(double v) => $"\"{Fmt(v)}\"";
    private static string Escape(string s) => RemoveDiacritics(s).Replace("\"", "\\\"");

    private static string RemoveDiacritics(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string form = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(form.Length);
        foreach (char c in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private readonly record struct DrillOrient(string Workplane, int RefPos, bool PatternAlongPrimary);

    private sealed record DrillOp(
        bool IsEdge,
        bool PatternAlongPrimary,
        string Workplane,
        int RefPos,
        string Id,
        string Description,
        int Pocet,
        double OdPredu,
        double Roztec,
        double Depth,
        double Dia,
        double? OrthoPos = null,
        string Tool = "E071");

    private sealed record AbsDrillOp(
        string Workplane,
        int RefPos,
        string Id,
        string Description,
        double X,
        double Y,
        double Depth,
        double Dia,
        int PatternCountX = 1,
        int PatternCountY = 1,
        double PitchX = 0,
        double PitchY = 0);
}
