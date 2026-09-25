using System.IO;
using System.Text.RegularExpressions;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>Kľúč / popisok skrinky z názvu Excelu (napr. …_skr2_… → skrinka 2).</summary>
internal static class SkrinkaNaming
{
    private static readonly Regex SkrNum = new(
        @"skr\s*[_-]?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void Apply(ExportDocument doc, string excelPath)
    {
        doc.ExcelPath = excelPath;
        string file = Path.GetFileNameWithoutExtension(excelPath) ?? "";
        var m = SkrNum.Match(file);
        if (m.Success)
        {
            string n = m.Groups[1].Value;
            doc.SkrinkaKey = "skr" + n;
            doc.SkrinkaLabel = "skrinka " + n;
        }
        else if (!string.IsNullOrWhiteSpace(doc.BlockName))
        {
            doc.SkrinkaKey = SanitizeKey(doc.BlockName);
            doc.SkrinkaLabel = doc.BlockName.Trim();
        }
        else
        {
            doc.SkrinkaKey = SanitizeKey(file);
            doc.SkrinkaLabel = string.IsNullOrWhiteSpace(file) ? "skrinka" : file;
        }

        foreach (var d in doc.Diely)
        {
            d.SkrinkaKey = doc.SkrinkaKey;
            d.SkrinkaLabel = doc.SkrinkaLabel;
        }

        foreach (var c in doc.Dotyky)
            c.SkrinkaKey = doc.SkrinkaKey;
    }

    private static string SanitizeKey(string s)
    {
        var chars = s.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        string key = new string(chars);
        while (key.Contains("__", StringComparison.Ordinal))
            key = key.Replace("__", "_", StringComparison.Ordinal);
        return string.IsNullOrEmpty(key) ? "skr" : key.Trim('_');
    }
}
