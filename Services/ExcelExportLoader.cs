using System.IO;
using CncPrieniky3D.Models;
using OfficeOpenXml;

namespace CncPrieniky3D.Services;

internal static class ExcelExportLoader
{
    /// <summary>Názvy / typy bez diakritiky — jednotné Contains/Equals v appke (chrbát→chrbat).</summary>
    private static string NormText(string? s)
        => PartRules.RemoveDiacritics(s).Trim();

    public static ExportDocument Load(string path)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage(new FileInfo(path));
        var doc = new ExportDocument();

        var kus = FindSheet(package, "Kusovnik", "Kusovník")
            ?? throw new InvalidOperationException("V Exceli chýba list „Kusovnik“.");

        doc.BlockName = kus.Cells[1, 2].Text?.Trim() ?? "";

        int headerRow = FindHeaderRow(kus, "Názov dielu");
        if (headerRow < 0)
            throw new InvalidOperationException("Na liste Kusovnik sa nenašiel riadok s hlavičkou.");

        var col = MapColumns(kus, headerRow);
        if (!col.TryGetValue("nazov", out int nazovCol) || nazovCol < 1)
            throw new InvalidOperationException("Chýba stĺpec „Názov dielu“.");

        for (int r = headerRow + 1; r <= (kus.Dimension?.End.Row ?? headerRow); r++)
        {
            string nazov = NormText(kus.Cells[r, nazovCol].Text);
            if (string.IsNullOrWhiteSpace(nazov))
                continue;

            var d = new DielecModel
            {
                Cislo = ToInt(Get(kus, r, col, "cislo"), doc.Diely.Count + 1),
                Nazov = nazov,
                Vrstva = Text(kus, r, col, "vrstva"),
                Handle = Text(kus, r, col, "handle"),
                RozmerX = ToDouble(Get(kus, r, col, "rx")),
                RozmerY = ToDouble(Get(kus, r, col, "ry")),
                RozmerZ = ToDouble(Get(kus, r, col, "rz")),
                AbsX1 = ToAbsFlag(Get(kus, r, col, "absx1")),
                AbsY1 = ToAbsFlag(Get(kus, r, col, "absy1")),
                AbsX2 = ToAbsFlag(Get(kus, r, col, "absx2")),
                AbsY2 = ToAbsFlag(Get(kus, r, col, "absy2")),
                MinX = ToDouble(Get(kus, r, col, "minx")),
                MinY = ToDouble(Get(kus, r, col, "miny")),
                MinZ = ToDouble(Get(kus, r, col, "minz")),
                WcsMinX = ToDouble(Get(kus, r, col, "wcsminx")),
                WcsMinY = ToDouble(Get(kus, r, col, "wcsminy")),
                WcsMinZ = ToDouble(Get(kus, r, col, "wcsminz")),
            };

            if (NearZero(d.WcsMinX) && NearZero(d.WcsMinY) && NearZero(d.WcsMinZ)
                && (!NearZero(d.MinX) || !NearZero(d.MinY) || !NearZero(d.MinZ)))
            {
                d.WcsMinX = d.MinX;
                d.WcsMinY = d.MinY;
                d.WcsMinZ = d.MinZ;
            }

            doc.Diely.Add(d);
        }

        NormalizePolicaInstanceNames(doc);
        // Pred CNC/Body: duplicitné názvy (skr2 2× „priečka, vrch“) → [dno]/[vrch] podľa Z.
        PartRules.DisambiguateDuplicateHorizontalNames(doc);

        var povodny = FindSheet(package, "Povodny kusovnik", "Pôvodný kusovník", "Povodny kusovník");
        if (povodny != null)
            AttachPovodnyKusovnik(povodny, doc);

        // Pred Body/Vyrezy — Diel č. šuflíkov nadväzuje na korpus (7, 8…).
        var sufleDiely = FindSheet(package, "Sufle diely", "Šufle diely");
        bool hasSufleDiely = sufleDiely != null && AttachSufleDiely(sufleDiely, doc);

        var body = FindSheet(package, "Body dielca", "Body");
        if (body != null)
            AttachBody(body, doc);

        var plochy = FindSheet(package, "Plochy dielca", "Plochy");
        if (plochy != null)
            AttachPlochy(plochy, doc);

        var vyrezy = FindSheet(package, "Vyrezy dielca", "Vyrezy");
        if (vyrezy != null)
            AttachVyrezy(vyrezy, doc);

        var prieniky = FindSheet(package, "Prieniky");
        if (prieniky != null)
            AttachDiery(prieniky, doc);

        var cnc = FindSheet(package, "Znacenie CNC", "CNC znacenie", "CNC značenie", "CNC");
        if (cnc != null)
            AttachCnc(cnc, doc);

        // CAD „znacenie CNC“ bez _zaves → Excel Typ=podperky, ale vzor System32 = závesy.
        ReclassifyZavesMislabelledAsPodperky(doc);

        // L = väčšie WCS Y (vľavo spredu), P = menšie Y. Značky ostávajú na solidе.
        NormalizeBokLpNamesByWcs(doc);

        // Starší export: malé značky na hladine „kovanie“ skončili v Prienikoch, nie v Znacenie CNC.
        if (prieniky != null)
            AttachKovaniePodperkyFromPrieniky(prieniky, doc);

        var sufle = FindSheet(package, "Sufle", "Šufle", "Sufliky");
        if (sufle != null)
            AttachSufle(sufle, doc, skipAgregaty: hasSufleDiely);

        // Čelá šuflíkov na prednú stranu (proti chrbátu / k prednej traverze).
        SuflikCeloNormalizer.Normalize(doc);

        // Nohy → automaticky 2. upnutie (_A Top kolíky / _B Bottom nohy).
        EnsureDruheUpnutieForNohy(doc);

        return doc;
    }

    /// <summary>
    /// Ak dielec má značenie CNC_nohy, zapne DruheUpnutie (prehlad vo vlastnostiach + _A/_B).
    /// </summary>
    public static void EnsureDruheUpnutieForNohy(ExportDocument doc)
    {
        foreach (var diel in doc.Diely)
        {
            if (!diel.CncZnacenia.Any(CncZnacenieTyp.IsNohy))
                continue;
            diel.DruheUpnutie = true;
            if (string.IsNullOrWhiteSpace(diel.XcsInfoMessageB))
                diel.XcsInfoMessageB = "po ABS - odsadit o listu - otoc";
        }
    }

    /// <summary>
    /// Starý export mal police ako L/P — pre rovnaké rozmery premenuj na 1, 2, 3…
    /// (pozícia v 3D ostáva, CNC sa zoskupí podľa základného názvu).
    /// </summary>
    private static void NormalizePolicaInstanceNames(ExportDocument doc)
    {
        foreach (var grp in doc.Diely
                     .Where(d => !d.JeSuflik && PartRules.IsPolicaName(d.Nazov))
                     .GroupBy(d => PartRules.CncGroupKey(d), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var list = grp.OrderBy(d => d.Cislo).ToList();
            string baseName = PartRules.PolicaBaseName(list[0].Nazov);
            for (int i = 0; i < list.Count; i++)
                list[i].Nazov = $"{baseName} {i + 1}";
        }
    }

    /// <summary>
    /// Hárok „Povodny kusovnik“ — CNC X/Y/hrúbka + ABS. AABB (Rozmer*) ostáva z Kusovníka.
    /// </summary>
    private static void AttachPovodnyKusovnik(ExcelWorksheet ws, ExportDocument doc)
    {
        int headerRow = FindHeaderRow(ws, "Názov dielu");
        if (headerRow < 0)
            headerRow = FindHeaderRow(ws, "CNC Rozmer X");
        if (headerRow < 0)
            return;

        var col = MapPovodnyColumns(ws, headerRow);
        if (!col.TryGetValue("nazov", out int nazovCol) || nazovCol < 1)
            return;

        var byCislo = doc.Diely
            .Where(d => !d.JeSuflik)
            .GroupBy(d => d.Cislo)
            .ToDictionary(g => g.Key, g => g.First());
        var byNazov = new Dictionary<string, DielecModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in doc.Diely.Where(d => !d.JeSuflik))
        {
            if (!byNazov.ContainsKey(d.Nazov ?? ""))
                byNazov[d.Nazov ?? ""] = d;
        }

        for (int r = headerRow + 1; r <= (ws.Dimension?.End.Row ?? headerRow); r++)
        {
            string nazov = NormText(ws.Cells[r, nazovCol].Text);
            if (string.IsNullOrWhiteSpace(nazov))
                continue;

            DielecModel? diel = null;
            int cislo = ToInt(Get(ws, r, col, "cislo"), 0);
            if (cislo > 0)
                byCislo.TryGetValue(cislo, out diel);
            if (diel == null)
                byNazov.TryGetValue(nazov, out diel);
            if (diel == null)
                continue;

            diel.CncRozmerX = ToDouble(Get(ws, r, col, "cncrx"));
            diel.CncRozmerY = ToDouble(Get(ws, r, col, "cncry"));
            diel.CncHrubka = ToDouble(Get(ws, r, col, "cnch"));

            // ABS zo zdroja CNC (rovnaké ako kusovník, ale preferuj tento hárok)
            if (col.TryGetValue("absx1", out int ax1) && ax1 > 0)
            {
                diel.AbsX1 = ToAbsFlag(Get(ws, r, col, "absx1"));
                diel.AbsY1 = ToAbsFlag(Get(ws, r, col, "absy1"));
                diel.AbsX2 = ToAbsFlag(Get(ws, r, col, "absx2"));
                diel.AbsY2 = ToAbsFlag(Get(ws, r, col, "absy2"));
            }

            if (diel.HasCncRozmery && !PartRules.AabbMatchesCnc(diel))
            {
                doc.LoadWarnings.Add(
                    $"{diel.Nazov}: AABB {diel.RozmerX:0.#}×{diel.RozmerY:0.#}×{diel.RozmerZ:0.#} " +
                    $"≠ CNC {diel.CncRozmerX:0.#}×{diel.CncRozmerY:0.#}×{diel.CncHrubka:0.#}");
            }
        }
    }

    private static Dictionary<string, int> MapPovodnyColumns(ExcelWorksheet ws, int headerRow)
    {
        var raw = HeaderMap(ws, headerRow);
        int FindExact(params string[] keys)
        {
            foreach (var k in keys)
            {
                string nk = NormText(k);
                foreach (var kv in raw)
                {
                    if (kv.Key.Equals(nk, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
            }
            return -1;
        }

        int Find(params string[] keys)
        {
            int exact = FindExact(keys);
            if (exact > 0) return exact;
            foreach (var kv in raw)
                foreach (var k in keys)
                    if (kv.Key.Contains(NormText(k), StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
            return -1;
        }

        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["cislo"] = Find("Č.", "C."),
            ["nazov"] = Find("Názov dielu", "Nazov"),
            ["cncrx"] = Find("CNC Rozmer X", "CNC X"),
            ["cncry"] = Find("CNC Rozmer Y", "CNC Y"),
            ["cnch"] = Find("CNC Hrúbka", "CNC Hrubka", "Hrúbka"),
            ["absx1"] = FindExact("ABS x1", "ABS X1"),
            ["absy1"] = FindExact("ABS y1", "ABS Y1"),
            ["absx2"] = FindExact("ABS x2", "ABS X2"),
            ["absy2"] = FindExact("ABS y2", "ABS Y2"),
        };
    }

    /// <summary>
    /// Hárok „Sufle diely“ — individuálne solidy šuflíkov (rovnaké Diel č. ako Body/Vyrezy).
    /// </summary>
    private static bool AttachSufleDiely(ExcelWorksheet ws, ExportDocument doc)
    {
        int headerRow = FindHeaderRow(ws, "Názov dielu");
        if (headerRow < 0) return false;

        var col = MapColumns(ws, headerRow);
        if (!col.TryGetValue("nazov", out int nazovCol) || nazovCol < 1)
            return false;

        int typCol = -1;
        int varCol = -1;
        {
            var map = HeaderMap(ws, headerRow);
            typCol = Col(map, "Typ");
            // „Typ“ môže chytiť „Typ dielu“ — preferuj exact krátky header
            foreach (var kv in map)
            {
                if (kv.Key.Equals("Typ", StringComparison.OrdinalIgnoreCase))
                {
                    typCol = kv.Value;
                    break;
                }
            }
            varCol = Col(map, "Varianta");
        }

        // WCS chýba v starších exportoch → posun z korpusu (WcsMin − Min).
        double ox = 0, oy = 0, oz = 0;
        bool hasOffset = false;
        var refKorpus = doc.Diely.FirstOrDefault(d => !d.JeSuflik);
        if (refKorpus != null
            && (!NearZero(refKorpus.WcsMinX) || !NearZero(refKorpus.WcsMinY) || !NearZero(refKorpus.WcsMinZ)
                || !NearZero(refKorpus.MinX) || !NearZero(refKorpus.MinY) || !NearZero(refKorpus.MinZ)))
        {
            ox = refKorpus.WcsMinX - refKorpus.MinX;
            oy = refKorpus.WcsMinY - refKorpus.MinY;
            oz = refKorpus.WcsMinZ - refKorpus.MinZ;
            hasOffset = !NearZero(ox) || !NearZero(oy) || !NearZero(oz);
        }

        int added = 0;
        for (int r = headerRow + 1; r <= (ws.Dimension?.End.Row ?? headerRow); r++)
        {
            string nazov = NormText(ws.Cells[r, nazovCol].Text);
            if (string.IsNullOrWhiteSpace(nazov)) continue;
            if (nazov.StartsWith("(ziadne", StringComparison.OrdinalIgnoreCase)) continue;

            int cislo = ToInt(Get(ws, r, col, "cislo"), 0);
            if (cislo < 1) continue;

            // Už môže existovať (duplicitný hárok) — neprepisuj korpus.
            if (doc.Diely.Any(d => d.Cislo == cislo && !d.JeSuflik))
                continue;
            if (doc.Diely.Any(d => d.Cislo == cislo && d.JeSuflik && !d.JeSuflikPozicia))
                continue;

            double minX = ToDouble(Get(ws, r, col, "minx"));
            double minY = ToDouble(Get(ws, r, col, "miny"));
            double minZ = ToDouble(Get(ws, r, col, "minz"));
            double wcsX = ToDouble(Get(ws, r, col, "wcsminx"));
            double wcsY = ToDouble(Get(ws, r, col, "wcsminy"));
            double wcsZ = ToDouble(Get(ws, r, col, "wcsminz"));

            if (NearZero(wcsX) && NearZero(wcsY) && NearZero(wcsZ))
            {
                if (hasOffset)
                {
                    wcsX = minX + ox;
                    wcsY = minY + oy;
                    wcsZ = minZ + oz;
                }
                else
                {
                    wcsX = minX;
                    wcsY = minY;
                    wcsZ = minZ;
                }
            }

            string typDielu = typCol > 0 ? NormText(ws.Cells[r, typCol].Text) : "";
            string varianta = varCol > 0 ? NormText(ws.Cells[r, varCol].Text) : "";

            doc.Diely.Add(new DielecModel
            {
                Cislo = cislo,
                Nazov = nazov,
                Vrstva = Text(ws, r, col, "vrstva"),
                Handle = Text(ws, r, col, "handle"),
                RozmerX = ToDouble(Get(ws, r, col, "rx")),
                RozmerY = ToDouble(Get(ws, r, col, "ry")),
                RozmerZ = ToDouble(Get(ws, r, col, "rz")),
                AbsX1 = ToAbsFlag(Get(ws, r, col, "absx1")),
                AbsY1 = ToAbsFlag(Get(ws, r, col, "absy1")),
                AbsX2 = ToAbsFlag(Get(ws, r, col, "absx2")),
                AbsY2 = ToAbsFlag(Get(ws, r, col, "absy2")),
                MinX = minX,
                MinY = minY,
                MinZ = minZ,
                WcsMinX = wcsX,
                WcsMinY = wcsY,
                WcsMinZ = wcsZ,
                JeSuflik = true,
                JeSuflikPozicia = false,
                SufelTypDielu = typDielu,
                SufelVarianta = varianta,
                PocetKusov = 1,
            });
            added++;
        }

        return added > 0;
    }

    private static void AttachSufle(ExcelWorksheet ws, ExportDocument doc, bool skipAgregaty = false)
    {
        int headerRow = FindHeaderRow(ws, "Typ riadku", "Názov");
        if (headerRow < 0) return;

        var map = HeaderMap(ws, headerRow);
        int cTyp = Col(map, "Typ riadku", "Typ");
        int cSufel = Col(map, "Šufel č.", "Sufel č.", "Šufel", "Sufel");
        int cTypDielu = Col(map, "Typ dielu");
        int cNazov = Col(map, "Názov", "Nazov");
        int cVar = Col(map, "Varianta");
        int cPocet = Col(map, "Počet", "Pocet");
        int cRx = Col(map, "Rozmer X");
        int cRy = Col(map, "Rozmer Y");
        int cRz = Col(map, "Rozmer Z");
        int cPx = ColExactOr(map, "Pos X");
        int cPy = ColExactOr(map, "Pos Y");
        int cPz = ColExactOr(map, "Pos Z");
        int cAbsX1 = Col(map, "ABS x1");
        int cAbsY1 = Col(map, "ABS y1");
        int cAbsX2 = Col(map, "ABS x2");
        int cAbsY2 = Col(map, "ABS y2");
        int cHandle = Col(map, "Handle");
        if (cTyp < 1 || cNazov < 1) return;

        int nextCislo = doc.Diely.Count == 0 ? 1 : doc.Diely.Max(d => d.Cislo) + 1;

        double ox = 0, oy = 0, oz = 0;
        bool hasOffset = false;
        var refKorpus = doc.Diely.FirstOrDefault(d => !d.JeSuflik);
        if (refKorpus != null)
        {
            ox = refKorpus.WcsMinX - refKorpus.MinX;
            oy = refKorpus.WcsMinY - refKorpus.MinY;
            oz = refKorpus.WcsMinZ - refKorpus.MinZ;
            hasOffset = !NearZero(ox) || !NearZero(oy) || !NearZero(oz);
        }

        for (int r = headerRow + 1; r <= (ws.Dimension?.End.Row ?? headerRow); r++)
        {
            string typRiadku = NormText(ws.Cells[r, cTyp].Text).ToLowerInvariant();
            string nazov = NormText(ws.Cells[r, cNazov].Text);
            if (string.IsNullOrWhiteSpace(nazov)) continue;
            if (nazov.StartsWith("(ziadne", StringComparison.OrdinalIgnoreCase)) continue;

            bool jePozicia = typRiadku.StartsWith("pozic", StringComparison.Ordinal);
            // Ak máme „Sufle diely“, agregáty „diel“ nepridávame (duplicita bez Body).
            if (skipAgregaty && !jePozicia) continue;

            double rx = cRx > 0 ? ToDouble(ws.Cells[r, cRx].Value) : 0;
            double ry = cRy > 0 ? ToDouble(ws.Cells[r, cRy].Value) : 0;
            double rz = cRz > 0 ? ToDouble(ws.Cells[r, cRz].Value) : 0;
            if (rx < 0.1 && ry < 0.1 && rz < 0.1) continue;

            double px = cPx > 0 ? ToDouble(ws.Cells[r, cPx].Value) : 0;
            double py = cPy > 0 ? ToDouble(ws.Cells[r, cPy].Value) : 0;
            double pz = cPz > 0 ? ToDouble(ws.Cells[r, cPz].Value) : 0;

            // pozicia: Pos = stred AABB v súradniciach bloku → Min roh; + WCS offset korpusu
            double wcsX = jePozicia ? px - rx * 0.5 : 0;
            double wcsY = jePozicia ? py - ry * 0.5 : 0;
            double wcsZ = jePozicia ? pz - rz * 0.5 : 0;
            if (jePozicia && hasOffset)
            {
                wcsX += ox;
                wcsY += oy;
                wcsZ += oz;
            }

            int pocet = cPocet > 0 ? ToInt(ws.Cells[r, cPocet].Value, 1) : 1;
            if (pocet < 1) pocet = 1;

            string varianta = cVar > 0 ? NormText(ws.Cells[r, cVar].Text) : "";
            string displayNazov = nazov;
            if (jePozicia && !string.IsNullOrEmpty(varianta))
                displayNazov = $"{nazov} ({varianta})";

            doc.Diely.Add(new DielecModel
            {
                Cislo = nextCislo++,
                Nazov = displayNazov,
                Vrstva = "sufel",
                Handle = cHandle > 0 ? (ws.Cells[r, cHandle].Text?.Trim() ?? "") : "",
                RozmerX = rx,
                RozmerY = ry,
                RozmerZ = rz,
                AbsX1 = cAbsX1 > 0 ? ToAbsFlag(ws.Cells[r, cAbsX1].Value) : 0,
                AbsY1 = cAbsY1 > 0 ? ToAbsFlag(ws.Cells[r, cAbsY1].Value) : 0,
                AbsX2 = cAbsX2 > 0 ? ToAbsFlag(ws.Cells[r, cAbsX2].Value) : 0,
                AbsY2 = cAbsY2 > 0 ? ToAbsFlag(ws.Cells[r, cAbsY2].Value) : 0,
                WcsMinX = wcsX,
                WcsMinY = wcsY,
                WcsMinZ = wcsZ,
                MinX = wcsX,
                MinY = wcsY,
                MinZ = wcsZ,
                JeSuflik = true,
                JeSuflikPozicia = jePozicia,
                SufelCislo = cSufel > 0 ? ToInt(ws.Cells[r, cSufel].Value, 0) : 0,
                SufelTypDielu = cTypDielu > 0 ? NormText(ws.Cells[r, cTypDielu].Text) : "",
                SufelVarianta = varianta,
                PocetKusov = pocet,
            });
        }
    }

    private static void AttachDiery(ExcelWorksheet ws, ExportDocument doc)
    {
        int headerRow = FindHeaderRow(ws, "Prienik č.", "Typ");
        if (headerRow < 0) return;

        var map = HeaderMap(ws, headerRow);
        int cDiel = Col(map, "Diel č.", "Diel");
        int cNazov = Col(map, "Názov dielu", "Nazov");
        int cCislo = Col(map, "Prienik č.", "Prienik");
        int cTyp = Col(map, "Typ");
        int cX = ColExactOr(map, "Pos X");
        int cY = ColExactOr(map, "Pos Y");
        int cZ = ColExactOr(map, "Pos Z");
        int cDia = Col(map, "Priemer");
        int cSirka = Col(map, "Šírka", "Sirka");
        int cVyska = Col(map, "Výška", "Vyska");
        int cDlzka = Col(map, "Dĺžka / hĺbka", "Dlzka", "Hĺbka", "Hlbka");
        if (cX < 1 || cTyp < 1) return;

        for (int r = headerRow + 1; r <= (ws.Dimension?.End.Row ?? headerRow); r++)
        {
            string typ = ws.Cells[r, cTyp].Text?.Trim() ?? "";
            if (!typ.StartsWith("Diera", StringComparison.OrdinalIgnoreCase))
                continue;

            string nazov = cNazov > 0 ? NormText(ws.Cells[r, cNazov].Text) : "";
            if (nazov.StartsWith("(ziadne", StringComparison.OrdinalIgnoreCase)) continue;

            var diel = FindDiel(doc, cDiel > 0 ? ToInt(ws.Cells[r, cDiel].Value, 0) : 0, nazov);
            if (diel == null) continue;

            diel.Diery.Add(new PrienikModel
            {
                Cislo = cCislo > 0 ? ToInt(ws.Cells[r, cCislo].Value, diel.Diery.Count + 1) : diel.Diery.Count + 1,
                Typ = typ,
                PosX = ToDouble(ws.Cells[r, cX].Value),
                PosY = cY > 0 ? ToDouble(ws.Cells[r, cY].Value) : 0,
                PosZ = cZ > 0 ? ToDouble(ws.Cells[r, cZ].Value) : 0,
                Priemer = cDia > 0 ? ToDouble(ws.Cells[r, cDia].Value) : 0,
                Sirka = cSirka > 0 ? ToDouble(ws.Cells[r, cSirka].Value) : 0,
                Vyska = cVyska > 0 ? ToDouble(ws.Cells[r, cVyska].Value) : 0,
                Hlbka = cDlzka > 0 ? ToDouble(ws.Cells[r, cDlzka].Value) : 0,
            });
        }
    }

    /// <summary>
    /// Typ=Kovanie s Ø≈3 a hĺbkou≈12 = podperky (vrstva môže byť kovanie alebo spotrebiče).
    /// Väčšie pánty/spotrebiče ostávajú len v Prienikoch.
    /// </summary>
    private static void AttachKovaniePodperkyFromPrieniky(ExcelWorksheet ws, ExportDocument doc)
    {
        int headerRow = FindHeaderRow(ws, "Prienik č.", "Typ");
        if (headerRow < 0) return;

        var map = HeaderMap(ws, headerRow);
        int cDiel = Col(map, "Diel č.", "Diel");
        int cNazov = Col(map, "Názov dielu", "Nazov");
        int cTyp = Col(map, "Typ");
        int cX = ColExactOr(map, "Pos X");
        int cY = ColExactOr(map, "Pos Y");
        int cZ = ColExactOr(map, "Pos Z");
        int cDia = Col(map, "Priemer");
        int cDlzka = Col(map, "Dĺžka / hĺbka", "Dlzka", "Hĺbka", "Hlbka");
        int cVrstva = Col(map, "Vrstva");
        int cHandle = Col(map, "Handle");
        if (cX < 1 || cTyp < 1) return;

        for (int r = headerRow + 1; r <= (ws.Dimension?.End.Row ?? headerRow); r++)
        {
            string typ = ws.Cells[r, cTyp].Text?.Trim() ?? "";
            if (!typ.StartsWith("Kovanie", StringComparison.OrdinalIgnoreCase))
                continue;

            double dia = cDia > 0 ? ToDouble(ws.Cells[r, cDia].Value) : 0;
            double hlbka = cDlzka > 0 ? ToDouble(ws.Cells[r, cDlzka].Value) : 0;
            // Rozhoduje geometria Ø3×12 — nie názov vrstvy (kovanie aj spotrebiče).
            if (!CncZnacenieTyp.MatchesPodperkyGeometry(dia, hlbka))
                continue;

            string vrstva = cVrstva > 0 ? (ws.Cells[r, cVrstva].Text?.Trim() ?? "") : "";
            string nazov = cNazov > 0 ? NormText(ws.Cells[r, cNazov].Text) : "";
            if (nazov.StartsWith("(ziadne", StringComparison.OrdinalIgnoreCase)) continue;

            var diel = FindDiel(doc, cDiel > 0 ? ToInt(ws.Cells[r, cDiel].Value, 0) : 0, nazov);
            if (diel == null) continue;

            double x = ToDouble(ws.Cells[r, cX].Value);
            double y = cY > 0 ? ToDouble(ws.Cells[r, cY].Value) : 0;
            double z = cZ > 0 ? ToDouble(ws.Cells[r, cZ].Value) : 0;

            bool already = diel.CncZnacenia.Any(z0 =>
                Math.Abs(z0.PosX - x) < 1.5
                && Math.Abs(z0.PosY - y) < 1.5
                && Math.Abs(z0.PosZ - z) < 1.5);
            if (already) continue;

            diel.CncZnacenia.Add(new CncZnacenieModel
            {
                Cislo = diel.CncZnacenia.Count + 1,
                Typ = CncZnacenieTyp.Podperky,
                PosX = x,
                PosY = y,
                PosZ = z,
                Priemer = dia,
                Hlbka = hlbka,
                Vrstva = vrstva,
                Handle = cHandle > 0 ? (ws.Cells[r, cHandle].Text?.Trim() ?? "") : "",
            });
        }
    }

    private static void AttachBody(ExcelWorksheet ws, ExportDocument doc)
    {
        // Pozor: v poznámke je text „Pos X/Y/Z“ – hlavičku hľadáme podľa „Bod č.“
        int headerRow = FindHeaderRow(ws, "Bod č.", "Bod c.", "Pos X");
        if (headerRow < 0) return;

        var map = HeaderMap(ws, headerRow);
        int cDiel = Col(map, "Diel č.", "Diel");
        int cNazov = Col(map, "Názov dielu", "Nazov");
        int cCislo = Col(map, "Bod č.", "Bod");
        int cX = ColExactOr(map, "Pos X");
        int cY = ColExactOr(map, "Pos Y");
        int cZ = ColExactOr(map, "Pos Z");
        if (cX < 1 || cY < 1 || cZ < 1)
            return;

        for (int r = headerRow + 1; r <= (ws.Dimension?.End.Row ?? headerRow); r++)
        {
            if (ws.Cells[r, cX].Value == null) continue;
            var diel = FindDiel(doc, cDiel > 0 ? ToInt(ws.Cells[r, cDiel].Value, 0) : 0,
                cNazov > 0 ? NormText(ws.Cells[r, cNazov].Text) : null);
            if (diel == null) continue;

            diel.Body.Add(new BodModel
            {
                Cislo = cCislo > 0 ? ToInt(ws.Cells[r, cCislo].Value, diel.Body.Count + 1) : diel.Body.Count + 1,
                PosX = ToDouble(ws.Cells[r, cX].Value),
                PosY = ToDouble(ws.Cells[r, cY].Value),
                PosZ = ToDouble(ws.Cells[r, cZ].Value),
            });
        }
    }

    private static void AttachVyrezy(ExcelWorksheet ws, ExportDocument doc)
    {
        int headerRow = FindHeaderRow(ws, "Výrez č.", "Vyrez");
        if (headerRow < 0)
            headerRow = FindHeaderRow(ws, "Tvar", "Typ");
        if (headerRow < 0) return;

        var map = HeaderMap(ws, headerRow);
        int cDiel = Col(map, "Diel č.", "Diel");
        int cNazov = Col(map, "Názov", "Nazov", "Názov dielu");
        int cVyrez = Col(map, "Výrez č.", "Vyrez č.", "Vyrez");
        int cTyp = Col(map, "Typ");
        int cTvar = Col(map, "Tvar");
        int cBod = Col(map, "Bod č.", "Bod");
        int cX = ColExactOr(map, "Pos X");
        int cY = ColExactOr(map, "Pos Y");
        int cZ = ColExactOr(map, "Pos Z");
        int cDia = Col(map, "Priemer");
        int cSirka = Col(map, "Šírka", "Sirka");
        int cVyska = Col(map, "Výška", "Vyska");
        int cHlbka = Col(map, "Hĺbka", "Hlbka");
        int cPozn = Col(map, "Poznámka", "Poznamka");
        if (cX < 1 || cY < 1 || cVyrez < 1)
            return;

        for (int r = headerRow + 1; r <= (ws.Dimension?.End.Row ?? headerRow); r++)
        {
            if (ws.Cells[r, cX].Value == null) continue;
            string nazov = cNazov > 0 ? NormText(ws.Cells[r, cNazov].Text) : "";
            if (nazov.StartsWith("(ziadne", StringComparison.OrdinalIgnoreCase)) continue;

            var diel = FindDiel(doc, cDiel > 0 ? ToInt(ws.Cells[r, cDiel].Value, 0) : 0, nazov);
            if (diel == null) continue;

            int vyrezCislo = ToInt(ws.Cells[r, cVyrez].Value, 0);
            if (vyrezCislo < 1) continue;

            var vyrez = diel.Vyrezy.FirstOrDefault(v => v.Cislo == vyrezCislo);
            if (vyrez == null)
            {
                vyrez = new VyrezModel
                {
                    Cislo = vyrezCislo,
                    Typ = cTyp > 0 ? (ws.Cells[r, cTyp].Text?.Trim() ?? "") : "",
                    Tvar = cTvar > 0 ? (ws.Cells[r, cTvar].Text?.Trim() ?? "") : "",
                    PosX = ToDouble(ws.Cells[r, cX].Value),
                    PosY = ToDouble(ws.Cells[r, cY].Value),
                    PosZ = cZ > 0 ? ToDouble(ws.Cells[r, cZ].Value) : 0,
                    Priemer = cDia > 0 ? ToDouble(ws.Cells[r, cDia].Value) : 0,
                    Sirka = cSirka > 0 ? ToDouble(ws.Cells[r, cSirka].Value) : 0,
                    Vyska = cVyska > 0 ? ToDouble(ws.Cells[r, cVyska].Value) : 0,
                    Hlbka = cHlbka > 0 ? ToDouble(ws.Cells[r, cHlbka].Value) : 0,
                    Poznamka = cPozn > 0 ? (ws.Cells[r, cPozn].Text?.Trim() ?? "") : "",
                };
                diel.Vyrezy.Add(vyrez);
            }

            int bodCislo = cBod > 0 ? ToInt(ws.Cells[r, cBod].Value, 0) : 0;
            // polygon = viac riadkov s Bod č.; kruh/hranaty môže mať Bod prázdny
            if (bodCislo > 0 || string.Equals(vyrez.Tvar, "polygon", StringComparison.OrdinalIgnoreCase))
            {
                if (bodCislo < 1) bodCislo = vyrez.Body.Count + 1;
                vyrez.Body.Add(new BodModel
                {
                    Cislo = bodCislo,
                    PosX = ToDouble(ws.Cells[r, cX].Value),
                    PosY = ToDouble(ws.Cells[r, cY].Value),
                    PosZ = cZ > 0 ? ToDouble(ws.Cells[r, cZ].Value) : 0,
                });
            }
            else
            {
                // jednoriadkový tvar — aktualizuj stred
                vyrez.PosX = ToDouble(ws.Cells[r, cX].Value);
                vyrez.PosY = ToDouble(ws.Cells[r, cY].Value);
                vyrez.PosZ = cZ > 0 ? ToDouble(ws.Cells[r, cZ].Value) : 0;
            }
        }

        foreach (var d in doc.Diely)
        {
            foreach (var v in d.Vyrezy)
            {
                if (v.Body.Count > 1)
                    v.Body.Sort((a, b) => a.Cislo.CompareTo(b.Cislo));
            }
            // Kruh + hranatý AABB tej istej diery → nechaj len kruh
            var keep = AabbVyrezMeshBuilder.PreferKruhOverRectDiery(d.Vyrezy).ToList();
            if (keep.Count != d.Vyrezy.Count)
            {
                d.Vyrezy.Clear();
                d.Vyrezy.AddRange(keep);
            }
            d.Vyrezy.Sort((a, b) => a.Cislo.CompareTo(b.Cislo));
        }
    }

    private static void AttachPlochy(ExcelWorksheet ws, ExportDocument doc)
    {
        int headerRow = FindHeaderRow(ws, "Plocha č.", "Plocha");
        if (headerRow < 0)
            headerRow = FindHeaderRow(ws, "Bod č.", "Typ");
        if (headerRow < 0) return;

        var map = HeaderMap(ws, headerRow);
        int cDiel = Col(map, "Diel č.", "Diel");
        int cNazov = Col(map, "Názov dielu", "Nazov");
        int cPlocha = Col(map, "Plocha č.", "Plocha");
        int cBod = Col(map, "Bod č.", "Bod");
        int cTyp = Col(map, "Typ");
        int cX = ColExactOr(map, "Pos X");
        int cY = ColExactOr(map, "Pos Y");
        int cZ = ColExactOr(map, "Pos Z");
        if (cX < 1 || cY < 1 || cZ < 1 || cPlocha < 1)
            return;

        for (int r = headerRow + 1; r <= (ws.Dimension?.End.Row ?? headerRow); r++)
        {
            if (ws.Cells[r, cX].Value == null) continue;
            string nazov = cNazov > 0 ? NormText(ws.Cells[r, cNazov].Text) : "";
            if (nazov.StartsWith("(ziadne", StringComparison.OrdinalIgnoreCase)) continue;

            var diel = FindDiel(doc, cDiel > 0 ? ToInt(ws.Cells[r, cDiel].Value, 0) : 0, nazov);
            if (diel == null) continue;

            string typ = cTyp > 0 ? (ws.Cells[r, cTyp].Text?.Trim() ?? "obrys") : "obrys";
            if (string.IsNullOrWhiteSpace(typ)) typ = "obrys";

            diel.Plochy.Add(new PlochaBodModel
            {
                PlochaCislo = ToInt(ws.Cells[r, cPlocha].Value, 1),
                BodCislo = cBod > 0 ? ToInt(ws.Cells[r, cBod].Value, diel.Plochy.Count + 1) : diel.Plochy.Count + 1,
                Typ = typ,
                PosX = ToDouble(ws.Cells[r, cX].Value),
                PosY = ToDouble(ws.Cells[r, cY].Value),
                PosZ = ToDouble(ws.Cells[r, cZ].Value),
            });
        }
    }

    private static void AttachCnc(ExcelWorksheet ws, ExportDocument doc)
    {
        int headerRow = FindHeaderRow(ws, "Značenie č.", "Znacenie", "Priemer");
        if (headerRow < 0)
            headerRow = FindHeaderRow(ws, "Pos X (na ploche)", "Pos X");
        if (headerRow < 0) return;

        var map = HeaderMap(ws, headerRow);
        int cDiel = Col(map, "Diel č.", "Diel");
        int cNazov = Col(map, "Názov dielu", "Nazov");
        int cCislo = Col(map, "Značenie č.", "Znacenie");
        int cTyp = Col(map, "Typ");
        int cX = ColExactOr(map, "Pos X (na ploche)", "Pos X");
        int cY = ColExactOr(map, "Pos Y (na ploche)", "Pos Y");
        int cZ = ColExactOr(map, "Pos Z (v hrúbke)", "Pos Z");
        int cDia = Col(map, "Priemer");
        int cHlbka = Col(map, "Hĺbka", "Hlbka", "Dĺžka / hĺbka", "Dlzka");
        int cVrstva = Col(map, "Vrstva");
        int cHandle = Col(map, "Handle");
        if (cX < 1) return;

        for (int r = headerRow + 1; r <= (ws.Dimension?.End.Row ?? headerRow); r++)
        {
            if (ws.Cells[r, cX].Value == null) continue;
            string nazov = cNazov > 0 ? NormText(ws.Cells[r, cNazov].Text) : "";
            if (nazov.StartsWith("(ziadne", StringComparison.OrdinalIgnoreCase)) continue;

            var diel = FindDiel(doc, cDiel > 0 ? ToInt(ws.Cells[r, cDiel].Value, 0) : 0, nazov);
            if (diel == null) continue;

            string vrstva = cVrstva > 0 ? (ws.Cells[r, cVrstva].Text?.Trim() ?? "") : "";
            string typ = cTyp > 0 ? (ws.Cells[r, cTyp].Text?.Trim() ?? "") : "";
            double dia = cDia > 0 ? ToDouble(ws.Cells[r, cDia].Value) : 3;
            double hlbka = cHlbka > 0 ? ToDouble(ws.Cells[r, cHlbka].Value) : 0;

            // Hladina kovanie/spotrebiče / Typ Kovanie → podperky len pri Ø3 × hĺbka 12.
            if (CncZnacenieTyp.IsPodperkyHardwareLayer(vrstva)
                || string.Equals(typ, "Kovanie", StringComparison.OrdinalIgnoreCase))
            {
                if (!CncZnacenieTyp.MatchesPodperkyGeometry(dia, hlbka))
                    continue; // pánt / iné kovanie — nie podperka
                typ = CncZnacenieTyp.Podperky;
            }
            else if (string.IsNullOrWhiteSpace(typ))
            {
                typ = CncZnacenieTyp.FromLayer(vrstva);
            }

            diel.CncZnacenia.Add(new CncZnacenieModel
            {
                Cislo = cCislo > 0 ? ToInt(ws.Cells[r, cCislo].Value, diel.CncZnacenia.Count + 1) : diel.CncZnacenia.Count + 1,
                Typ = typ,
                PosX = ToDouble(ws.Cells[r, cX].Value),
                PosY = cY > 0 ? ToDouble(ws.Cells[r, cY].Value) : 0,
                PosZ = cZ > 0 ? ToDouble(ws.Cells[r, cZ].Value) : 0,
                Priemer = dia,
                Hlbka = hlbka,
                Vrstva = vrstva,
                Handle = cHandle > 0 ? (ws.Cells[r, cHandle].Text?.Trim() ?? "") : "",
            });
        }
    }

    /// <summary>
    /// Na bokoch: značky z holého „znacenie CNC“ (nie kovanie) s párom stĺpcov ~32 mm
    /// (System 32 predznačenie) preklasifikuj z podperiek na závesy.
    /// </summary>
    private static void ReclassifyZavesMislabelledAsPodperky(ExportDocument doc)
    {
        foreach (var diel in doc.KorpusDiely)
        {
            if (PartRules.DetectRole(diel.Nazov) is not ("bokL" or "bokP"))
                continue;

            var candidates = diel.CncZnacenia
                .Where(z => CncZnacenieTyp.IsPlainZnacenieCncLayer(z.Vrstva))
                .Where(z => !CncZnacenieTyp.IsAnyZaves(z))
                .Where(z => CncZnacenieTyp.IsPodperky(z)
                            || string.Equals(z.Typ, CncZnacenieTyp.Podperky, StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrWhiteSpace(z.Typ))
                .ToList();
            if (candidates.Count < 4)
                continue;
            if (!LooksLikeSystem32ZavesPair(candidates))
                continue;

            foreach (var z in candidates)
                z.Typ = CncZnacenieTyp.ZavesRady;
        }
    }

    /// <summary>Dva stĺpce Pos X (alebo Y) s rozstupom ≈ 32 mm — predznačenie závesov.</summary>
    private static bool LooksLikeSystem32ZavesPair(List<CncZnacenieModel> pts)
    {
        static List<double> DistinctRounded(IEnumerable<double> vals) =>
            vals.Select(v => Math.Round(v * 2.0) / 2.0).Distinct().OrderBy(v => v).ToList();

        var xs = DistinctRounded(pts.Select(p => p.PosX));
        var ys = DistinctRounded(pts.Select(p => p.PosY));

        bool Pair32(List<double> a) =>
            a.Count == 2 && Math.Abs(a[1] - a[0] - 32.0) <= 2.5;

        // System 32: pár dier 32 mm (os X alebo Y na ploche).
        return Pair32(xs) || Pair32(ys);
    }

    /// <summary>
    /// Zosúladí príponu L/P v názve s vonkajšou plochou (WCS Y).
    /// Značky ostávajú na solidе; Cislo sa nemení.
    /// </summary>
    private static void NormalizeBokLpNamesByWcs(ExportDocument doc)
    {
        foreach (var d in doc.KorpusDiely)
        {
            if (!PartRules.IsSidePanelCandidate(d))
                continue;

            string role = PartRules.DetectRole(d, doc);
            if (role is not ("bokL" or "bokP"))
                continue;

            string want = role == "bokL" ? "L" : "P";
            string nazov = d.Nazov ?? "";
            string n = PartRules.StripDiacritics(nazov);
            bool hasL = n.Contains("bok") && (n.Contains(" l") || n.EndsWith("l") ||
                System.Text.RegularExpressions.Regex.IsMatch(nazov, @"\bL\b"));
            bool hasP = n.Contains("bok") && (n.Contains(" p") || n.EndsWith("p") ||
                System.Text.RegularExpressions.Regex.IsMatch(nazov, @"\bP\b"));

            if (want == "L" && hasL && !hasP) continue;
            if (want == "P" && hasP && !hasL) continue;

            d.Nazov = ReplaceBokLpSuffix(nazov, want);
        }
    }

    private static string ReplaceBokLpSuffix(string nazov, string lp)
    {
        string s = System.Text.RegularExpressions.Regex.Replace(
            nazov.TrimEnd(),
            @"\s*[LPlp]\s*$",
            "",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        s = s.TrimEnd();
        // „bok 2P“ / „bok2 P“
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"([Bb]ok\w*)\s*[LPlp]\b",
            "$1",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return $"{s.TrimEnd()} {lp}";
    }

    private static DielecModel? FindDiel(ExportDocument doc, int cislo, string? nazov)
    {
        // 1) Diel č. — jednoznačné (skr2: rovnaký názov na dne aj vrchu).
        if (cislo > 0)
        {
            var byCislo = doc.Diely.FirstOrDefault(d => d.Cislo == cislo);
            if (byCislo != null)
                return byCislo;
        }

        // 2) Názov — po Disambiguate už unikátny; prefix match ak Excel ešte nemá [dno]/[vrch].
        string key = NormText(nazov);
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var exact = doc.Diely.Where(d =>
            string.Equals(NormText(d.Nazov), key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
            return exact[0];
        if (exact.Count > 1)
            return exact[0];

        // Excel „13_priecka, vrch 2“ vs app „13_priecka, vrch 2 [dno]“
        var prefixed = doc.Diely.Where(d =>
        {
            string dn = NormText(d.Nazov);
            return dn.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                   && (dn.Length == key.Length
                       || dn[key.Length] is ' ' or '[' or '(' or '#');
        }).ToList();
        if (prefixed.Count == 1)
            return prefixed[0];
        if (prefixed.Count > 1 && cislo > 0)
            return prefixed.FirstOrDefault(d => d.Cislo == cislo) ?? prefixed[0];
        return prefixed.FirstOrDefault();
    }

    private static ExcelWorksheet? FindSheet(ExcelPackage package, params string[] names)
    {
        foreach (var n in names)
        {
            string nn = NormText(n);
            var ws = package.Workbook.Worksheets.FirstOrDefault(w =>
                string.Equals(NormText(w.Name), nn, StringComparison.OrdinalIgnoreCase));
            if (ws != null) return ws;
        }
        return package.Workbook.Worksheets.FirstOrDefault(w =>
        {
            string wn = NormText(w.Name);
            return names.Any(n => wn.Contains(NormText(n), StringComparison.OrdinalIgnoreCase));
        });
    }

    private static int FindHeaderRow(ExcelWorksheet ws, params string[] mustContainAny)
    {
        int maxR = Math.Min(ws.Dimension?.End.Row ?? 1, 40);
        int maxC = ws.Dimension?.End.Column ?? 1;
        var keys = mustContainAny.Select(NormText).Where(k => k.Length > 0).ToArray();

        // Preferuj riadok, kde je zhoda celého textu bunky (nie substring v poznámke)
        for (int r = 1; r <= maxR; r++)
        {
            int hits = 0;
            for (int c = 1; c <= maxC; c++)
            {
                string t = NormText(ws.Cells[r, c].Text);
                if (t.Length == 0) continue;
                foreach (var key in keys)
                {
                    if (t.Equals(key, StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        hits++;
                        break;
                    }
                }
            }
            if (hits > 0)
                return r;
        }

        // Fallback: substring (staré súbory)
        for (int r = 1; r <= maxR; r++)
        {
            for (int c = 1; c <= maxC; c++)
            {
                string t = NormText(ws.Cells[r, c].Text);
                foreach (var key in keys)
                {
                    if (t.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return r;
                }
            }
        }
        return -1;
    }

    private static int ColExactOr(Dictionary<string, int> map, params string[] names)
    {
        foreach (var n in names)
        {
            string nn = NormText(n);
            if (map.TryGetValue(nn, out int c)) return c;
            foreach (var kv in map)
                if (kv.Key.Equals(nn, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
        }
        // Až potom partial – ale nie poznámkové texty
        foreach (var n in names)
        {
            string nn = NormText(n);
            foreach (var kv in map)
            {
                if (kv.Key.Length <= nn.Length + 20
                    && kv.Key.StartsWith(nn, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
        }
        return -1;
    }

    private static Dictionary<string, int> HeaderMap(ExcelWorksheet ws, int headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int cols = ws.Dimension?.End.Column ?? 0;
        for (int c = 1; c <= cols; c++)
        {
            string h = NormText(ws.Cells[headerRow, c].Text);
            if (h.Length > 0) map[h] = c;
        }
        return map;
    }

    private static int Col(Dictionary<string, int> map, params string[] names)
    {
        foreach (var n in names)
        {
            string nn = NormText(n);
            if (map.TryGetValue(nn, out int c)) return c;
            foreach (var kv in map)
                if (kv.Key.StartsWith(nn, StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Contains(nn, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
        }
        return -1;
    }

    private static Dictionary<string, int> MapColumns(ExcelWorksheet ws, int headerRow)
    {
        var raw = HeaderMap(ws, headerRow);
        int FindExact(params string[] keys)
        {
            foreach (var k in keys)
            {
                string nk = NormText(k);
                foreach (var kv in raw)
                {
                    if (kv.Key.Equals(nk, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
            }
            return -1;
        }

        int Find(params string[] keys)
        {
            int exact = FindExact(keys);
            if (exact > 0) return exact;
            foreach (var kv in raw)
                foreach (var k in keys)
                    if (kv.Key.Contains(NormText(k), StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
            return -1;
        }

        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["cislo"] = Find("Č.", "C."),
            ["nazov"] = Find("Názov dielu", "Nazov"),
            ["vrstva"] = Find("Vrstva"),
            ["handle"] = Find("Handle"),
            ["rx"] = Find("Rozmer X"),
            ["ry"] = Find("Rozmer Y"),
            ["rz"] = Find("Rozmer Z"),
            // ABS len exact — fuzzy by chytilo „Max X“ / „Rozmer X“
            ["absx1"] = FindExact("ABS x1", "ABS X1"),
            ["absy1"] = FindExact("ABS y1", "ABS Y1"),
            ["absx2"] = FindExact("ABS x2", "ABS X2"),
            ["absy2"] = FindExact("ABS y2", "ABS Y2"),
            ["minx"] = Find("Min X"),
            ["miny"] = Find("Min Y"),
            ["minz"] = Find("Min Z"),
            ["wcsminx"] = Find("WCS Min X"),
            ["wcsminy"] = Find("WCS Min Y"),
            ["wcsminz"] = Find("WCS Min Z"),
        };
    }

    private static int ToAbsFlag(object? v)
    {
        if (v == null) return 0;
        string text = Convert.ToString(v)?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (double.TryParse(text.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double d))
            return d >= 0.5 ? 1 : 0;
        return text is "x" or "X" or "ano" or "áno" or "yes"
               || string.Equals(NormText(text), "ano", StringComparison.OrdinalIgnoreCase)
            ? 1 : 0;
    }

    private static object? Get(ExcelWorksheet ws, int r, Dictionary<string, int> col, string key)
        => col.TryGetValue(key, out int c) && c > 0 ? ws.Cells[r, c].Value : null;

    private static string Text(ExcelWorksheet ws, int r, Dictionary<string, int> col, string key)
        => col.TryGetValue(key, out int c) && c > 0 ? (ws.Cells[r, c].Text?.Trim() ?? "") : "";

    private static bool NearZero(double v) => Math.Abs(v) < 1e-9;

    private static double ToDouble(object? v)
    {
        if (v == null) return 0;
        if (v is double d) return d;
        if (v is float f) return f;
        if (v is decimal m) return (double)m;
        if (v is int i) return i;
        if (double.TryParse(Convert.ToString(v), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double x))
            return x;
        if (double.TryParse(Convert.ToString(v), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.CurrentCulture, out x))
            return x;
        return 0;
    }

    private static int ToInt(object? v, int fallback)
    {
        if (v == null) return fallback;
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (v is double d) return (int)Math.Round(d);
        if (v is float f) return (int)Math.Round(f);
        if (v is decimal m) return (int)Math.Round(m);
        string s = Convert.ToString(v)?.Trim() ?? "";
        if (int.TryParse(s, out int x)) return x;
        if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double xd))
            return (int)Math.Round(xd);
        if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.CurrentCulture, out xd))
            return (int)Math.Round(xd);
        return fallback;
    }
}
