using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Media3D;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Uloženie / načítanie pracovnej relácie (kolíky, skrutky, vlastnosti dielov)
/// vedľa Excel exportu — *.cnc3d.json. Geometria sa vždy berie znova z Excelu.
/// </summary>
internal static class ProjectSessionStore
{
    public const string Extension = ".cnc3d.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string SessionPathForExcel(string excelPath)
    {
        string? dir = Path.GetDirectoryName(excelPath);
        string name = Path.GetFileNameWithoutExtension(excelPath);
        return Path.Combine(dir ?? ".", name + Extension);
    }

    public static bool ExistsForExcel(string excelPath)
        => File.Exists(SessionPathForExcel(excelPath));

    public static void Save(string excelPath, ExportDocument doc)
    {
        var dto = Capture(excelPath, doc);
        string path = SessionPathForExcel(excelPath);
        string json = JsonSerializer.Serialize(dto, JsonOpts);
        File.WriteAllText(path, json);
    }

    public static ProjectSessionDto? TryLoadFile(string sessionPath)
    {
        if (!File.Exists(sessionPath)) return null;
        string json = File.ReadAllText(sessionPath);
        return JsonSerializer.Deserialize<ProjectSessionDto>(json, JsonOpts);
    }

    public static ProjectSessionDto Capture(string excelPath, ExportDocument doc)
    {
        var dto = new ProjectSessionDto
        {
            Version = 1,
            ExcelPath = excelPath,
            BlockName = doc.BlockName,
            SavedAt = DateTime.Now
        };

        foreach (var d in doc.Diely)
        {
            dto.Diely.Add(new DielecSessionDto
            {
                Cislo = d.Cislo,
                Nazov = d.Nazov,
                OtocitSpodkomHore = d.OtocitSpodkomHore,
                JeSkryty = d.JeSkryty,
                AbsJeDole = d.AbsJeDole,
                AbsPaskaMm = d.AbsPaskaMm,
                XcsInfoMessage = d.XcsInfoMessage,
                DruheUpnutie = d.DruheUpnutie,
                XcsInfoMessageB = d.XcsInfoMessageB,
                PocetKolikov = d.PocetKolikov
            });
        }

        foreach (var c in doc.Dotyky.Where(x => !x.JeSuflikAuto))
        {
            dto.Dotyky.Add(new ContactSessionDto
            {
                Cislo = c.Cislo,
                PartA = c.PartA,
                PartB = c.PartB,
                Axis = c.Axis,
                Oznacenie = c.Oznacenie,
                Oznaceny = c.Oznaceny,
                CenterX = c.Center.X,
                CenterY = c.Center.Y,
                CenterZ = c.Center.Z,
                Koliky = c.KolikSerie.Select(s => new KolikSerieDto
                {
                    Cislo = s.Cislo,
                    OdPredu = s.OdPredu,
                    PocetKolikov = s.PocetKolikov,
                    RoztecKolikov = s.RoztecKolikov,
                    ZDruhejStrany = s.ZDruhejStrany,
                    ZoStredu = s.ZoStredu
                }).ToList(),
                Skrutky = c.SkrutkySerie.Select(s => new SkrutkaSerieDto
                {
                    Cislo = s.Cislo,
                    OdPredu = s.OdPredu,
                    PocetSkrutiek = s.PocetSkrutiek,
                    RoztecSkrutiek = s.RoztecSkrutiek,
                    ZDruhejStrany = s.ZDruhejStrany,
                    Symetricke = s.Symetricke,
                    SymetriaMedziKolikmi = s.SymetriaMedziKolikmi
                }).ToList()
            });
        }

        return dto;
    }

    /// <summary>
    /// Aplikuje reláciu na už načítaný Excel dokument (po ContactDetector).
    /// </summary>
    public static int Apply(ExportDocument doc, ProjectSessionDto session)
    {
        int applied = 0;

        foreach (var sd in session.Diely)
        {
            var d = FindDielec(doc, sd.Cislo, sd.Nazov);
            if (d == null) continue;
            d.OtocitSpodkomHore = sd.OtocitSpodkomHore;
            d.JeSkryty = sd.JeSkryty;
            d.AbsJeDole = sd.AbsJeDole;
            if (sd.AbsPaskaMm > 0) d.AbsPaskaMm = sd.AbsPaskaMm;
            d.XcsInfoMessage = sd.XcsInfoMessage ?? "";
            d.DruheUpnutie = sd.DruheUpnutie;
            if (!string.IsNullOrWhiteSpace(sd.XcsInfoMessageB))
                d.XcsInfoMessageB = sd.XcsInfoMessageB;
            d.PocetKolikov = sd.PocetKolikov;
            applied++;
        }

        foreach (var sc in session.Dotyky)
        {
            var c = FindContact(doc, sc);
            if (c == null) continue;

            c.Oznacenie = sc.Oznacenie ?? "";
            c.Oznaceny = sc.Oznaceny || !string.IsNullOrWhiteSpace(c.Oznacenie);

            if (sc.Koliky is { Count: > 0 })
            {
                c.ReplaceAllSerie(sc.Koliky.Select(k => new KolikSerie
                {
                    Cislo = k.Cislo,
                    OdPredu = k.OdPredu,
                    PocetKolikov = k.PocetKolikov,
                    RoztecKolikov = k.RoztecKolikov,
                    ZDruhejStrany = k.ZDruhejStrany,
                    ZoStredu = k.ZoStredu
                }));
            }

            if (sc.Skrutky is { Count: > 0 })
            {
                c.ReplaceAllSkrutkySerie(sc.Skrutky.Select(k => new SkrutkaSerie
                {
                    Cislo = k.Cislo,
                    OdPredu = k.OdPredu,
                    PocetSkrutiek = k.PocetSkrutiek,
                    RoztecSkrutiek = k.RoztecSkrutiek,
                    ZDruhejStrany = k.ZDruhejStrany,
                    Symetricke = k.Symetricke,
                    SymetriaMedziKolikmi = k.SymetriaMedziKolikmi
                }));
            }

            applied++;
        }

        // Obnov kolíky šuflíkov podľa uloženého PocetKolikov
        foreach (var poz in doc.SuflikDiely.Where(d => d.JeSuflikPozicia && d.PocetKolikov > 0))
            SuflikContactBuilder.RefreshKolikyForPozicia(doc, poz);

        return applied;
    }

    private static DielecModel? FindDielec(ExportDocument doc, int cislo, string? nazov)
    {
        if (cislo > 0)
        {
            var byNum = doc.Diely.FirstOrDefault(d => d.Cislo == cislo);
            if (byNum != null) return byNum;
        }
        if (!string.IsNullOrWhiteSpace(nazov))
        {
            return doc.Diely.FirstOrDefault(d =>
                string.Equals(d.Nazov, nazov, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private static ContactMark? FindContact(ExportDocument doc, ContactSessionDto sc)
    {
        static bool Same(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Presná zhoda A↔B + os
        var match = doc.Dotyky.FirstOrDefault(c =>
            !c.JeSuflikAuto
            && c.Axis == sc.Axis
            && ((Same(c.PartA, sc.PartA) && Same(c.PartB, sc.PartB))
                || (Same(c.PartA, sc.PartB) && Same(c.PartB, sc.PartA))));
        if (match != null) return match;

        // Fallback: len názvy dielov
        match = doc.Dotyky.FirstOrDefault(c =>
            !c.JeSuflikAuto
            && ((Same(c.PartA, sc.PartA) && Same(c.PartB, sc.PartB))
                || (Same(c.PartA, sc.PartB) && Same(c.PartB, sc.PartA))));
        if (match != null) return match;

        // Fallback: najbližší stred (ak sa prepočítali kontakty)
        if (sc.CenterX != 0 || sc.CenterY != 0 || sc.CenterZ != 0)
        {
            var target = new Point3D(sc.CenterX, sc.CenterY, sc.CenterZ);
            return doc.Dotyky
                .Where(c => !c.JeSuflikAuto)
                .OrderBy(c => (c.Center - target).LengthSquared)
                .FirstOrDefault(c => (c.Center - target).Length < 25);
        }

        return null;
    }

    public sealed class ProjectSessionDto
    {
        public int Version { get; set; } = 1;
        public string? ExcelPath { get; set; }
        public string? BlockName { get; set; }
        public DateTime SavedAt { get; set; }
        public List<DielecSessionDto> Diely { get; set; } = new();
        public List<ContactSessionDto> Dotyky { get; set; } = new();
    }

    public sealed class DielecSessionDto
    {
        public int Cislo { get; set; }
        public string? Nazov { get; set; }
        public bool OtocitSpodkomHore { get; set; }
        public bool JeSkryty { get; set; }
        public bool AbsJeDole { get; set; }
        public double AbsPaskaMm { get; set; }
        public string? XcsInfoMessage { get; set; }
        public bool DruheUpnutie { get; set; }
        public string? XcsInfoMessageB { get; set; }
        public int PocetKolikov { get; set; }
    }

    public sealed class ContactSessionDto
    {
        public int Cislo { get; set; }
        public string PartA { get; set; } = "";
        public string PartB { get; set; } = "";
        public int Axis { get; set; }
        public string? Oznacenie { get; set; }
        public bool Oznaceny { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double CenterZ { get; set; }
        public List<KolikSerieDto>? Koliky { get; set; }
        public List<SkrutkaSerieDto>? Skrutky { get; set; }
    }

    public sealed class KolikSerieDto
    {
        public int Cislo { get; set; }
        public double OdPredu { get; set; }
        public int PocetKolikov { get; set; }
        public double RoztecKolikov { get; set; }
        public bool ZDruhejStrany { get; set; }
        public bool ZoStredu { get; set; }
    }

    public sealed class SkrutkaSerieDto
    {
        public int Cislo { get; set; }
        public double OdPredu { get; set; }
        public int PocetSkrutiek { get; set; }
        public double RoztecSkrutiek { get; set; }
        public bool ZDruhejStrany { get; set; }
        public bool Symetricke { get; set; }
        public bool SymetriaMedziKolikmi { get; set; }
    }
}
