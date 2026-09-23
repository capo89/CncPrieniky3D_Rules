using System.IO;
using CncPrieniky3D.Models;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace CncPrieniky3D.Services;

internal static class GeneratedDrillExcelExporter
{
    public static void Save(string filePath, ExportDocument doc, IReadOnlyList<GeneratedDrill> drills)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        using var package = new ExcelPackage();
        WriteVrtania(package, doc, drills);
        WriteCncZnacenie(package, doc);

        var fi = new FileInfo(filePath);
        if (fi.Exists)
            fi.Delete();
        package.SaveAs(fi);
    }

    private static void WriteVrtania(ExcelPackage package, ExportDocument doc, IReadOnlyList<GeneratedDrill> drills)
    {
        var ws = package.Workbook.Worksheets.Add("Vrtania");
        ws.Cells[1, 1].Value = "Blok";
        ws.Cells[1, 2].Value = doc.BlockName;
        ws.Cells[2, 1].Value = "Dátum";
        ws.Cells[2, 2].Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        ws.Cells[3, 1].Value = "Poznámka";
        ws.Cells[3, 2].Value =
            "Vygenerované z kolíkov/skrutiek. Local = od WCS Min dielca. Face = CNC súradnice plochy. " +
            "Kolík Ø8: 13 mm plocha / 23 mm hrana. Skrutka Ø3: cez hrúbku plochy.";

        int headerRow = 5;
        string[] headers =
        {
            "Diel č.", "Názov dielu", "Typ", "Strana", "Dotyk",
            "Local X", "Local Y", "Local Z",
            "Face X", "Face Y", "Face Z (hrúbka)",
            "Priemer", "Hĺbka"
        };
        for (int c = 0; c < headers.Length; c++)
            ws.Cells[headerRow, c + 1].Value = headers[c];
        StyleHeader(ws, headerRow, headers.Length);

        int row = headerRow + 1;
        foreach (var d in drills.OrderBy(x => x.DielCislo).ThenBy(x => x.Typ).ThenBy(x => x.Dotyk))
        {
            ws.Cells[row, 1].Value = d.DielCislo;
            ws.Cells[row, 2].Value = d.DielNazov;
            ws.Cells[row, 3].Value = d.Typ;
            ws.Cells[row, 4].Value = d.Strana;
            ws.Cells[row, 5].Value = d.Dotyk;
            ws.Cells[row, 6].Value = Math.Round(d.LocalX, 3);
            ws.Cells[row, 7].Value = Math.Round(d.LocalY, 3);
            ws.Cells[row, 8].Value = Math.Round(d.LocalZ, 3);
            ws.Cells[row, 9].Value = Math.Round(d.FaceX, 3);
            ws.Cells[row, 10].Value = Math.Round(d.FaceY, 3);
            ws.Cells[row, 11].Value = Math.Round(d.FaceZ, 3);
            ws.Cells[row, 12].Value = d.Priemer;
            ws.Cells[row, 13].Value = Math.Round(d.Hlbka, 3);
            row++;
        }

        if (row == headerRow + 1)
            ws.Cells[row, 1].Value = "(žiadne vŕtania — najprv Kolíkovať / Skrutky)";

        if (ws.Dimension != null)
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
    }

    private static void WriteCncZnacenie(ExcelPackage package, ExportDocument doc)
    {
        var ws = package.Workbook.Worksheets.Add("CNC znacenie");
        ws.Cells[1, 1].Value = "Blok";
        ws.Cells[1, 2].Value = doc.BlockName;
        ws.Cells[2, 1].Value = "Poznámka";
        ws.Cells[2, 2].Value = "Vrátane GEN: vŕtaní z Generovať.";

        int headerRow = 4;
        string[] headers =
        {
            "Diel č.", "Názov dielu", "Značenie č.", "Typ",
            "Pos X (na ploche)", "Pos Y (na ploche)", "Pos Z (v hrúbke)",
            "Priemer", "Vrstva", "Handle"
        };
        for (int c = 0; c < headers.Length; c++)
            ws.Cells[headerRow, c + 1].Value = headers[c];
        StyleHeader(ws, headerRow, headers.Length);

        int row = headerRow + 1;
        foreach (var d in doc.Diely)
        {
            foreach (var z in d.CncZnacenia)
            {
                ws.Cells[row, 1].Value = d.Cislo;
                ws.Cells[row, 2].Value = d.Nazov;
                ws.Cells[row, 3].Value = z.Cislo;
                ws.Cells[row, 4].Value = z.ResolvedTyp;
                ws.Cells[row, 5].Value = z.PosX;
                ws.Cells[row, 6].Value = z.PosY;
                ws.Cells[row, 7].Value = z.PosZ;
                ws.Cells[row, 8].Value = z.Priemer;
                ws.Cells[row, 9].Value = z.Vrstva;
                ws.Cells[row, 10].Value = z.Handle;
                row++;
            }
        }

        if (row == headerRow + 1)
            ws.Cells[row, 1].Value = "(žiadne CNC značenie)";

        if (ws.Dimension != null)
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
    }

    private static void StyleHeader(ExcelWorksheet ws, int headerRow, int cols)
    {
        using var range = ws.Cells[headerRow, 1, headerRow, cols];
        range.Style.Font.Bold = true;
        range.Style.Fill.PatternType = ExcelFillStyle.Solid;
        range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(220, 220, 220));
    }
}
