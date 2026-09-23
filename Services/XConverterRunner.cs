using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace CncPrieniky3D.Services;

/// <summary>
/// Spustenie SCM XConverter.exe (.xcs → .pgmx) — podľa E:\_SCRIPTAS\XConverter.ps1.
/// </summary>
internal static class XConverterRunner
{
    private const string PrimarySearchRoot = @"C:\Program Files (x86)\SCM Group";
    private const string PrimaryTlgxDir = @"C:\Program Files (x86)\SCM Group\Maestro\Tlgx";
    private const string DefaultTlgxPath = @"C:\Program Files (x86)\SCM Group\Maestro\Tlgx\def.tlgx";
    private static readonly string[] KnownExePaths =
    {
        @"C:\Program Files (x86)\SCM Group\Maestro\XConverter.exe",
        @"C:\Program Files\SCM Group\Maestro\XConverter.exe",
        @"C:\SCM Group\Maestro\XConverter.exe",
    };
    private static readonly string[] KnownTlgxPaths =
    {
        @"C:\Program Files (x86)\SCM Group\Maestro\Tlgx\def.tlgx",
        @"C:\Program Files\SCM Group\Maestro\Tlgx\def.tlgx",
        @"C:\SCM Group\Maestro\Tlgx\def.tlgx",
    };

    public sealed record Result(bool Ok, string Message, string? LogPath);

    /// <summary>
    /// Konverzia priečinka s .xcs. Pri viacerých .tlgx ponúkne výber.
    /// </summary>
    public static Result ConvertFolder(string inputDir, Window? owner = null, string? outputDir = null)
    {
        if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
            return new Result(false, "Vstupný priečinok neexistuje.", null);

        outputDir ??= inputDir;

        string? exe = FindXConverter();
        if (exe == null)
        {
            return new Result(false,
                "XConverter.exe sa nepodarilo nájsť.\nSkontrolujte, či je nainštalovaný Maestro (SCM Group).",
                null);
        }

        var tlgxFiles = FindTlgxFiles(inputDir);
        if (tlgxFiles.Count == 0)
        {
            return new Result(false,
                "Nenašli sa žiadne *.tlgx súbory.\n\n" +
                $"Predvolené: {DefaultTlgxPath}\n" +
                $"Priečinok: {PrimaryTlgxDir}\n" +
                $"Vstup: {inputDir}",
                null);
        }

        string? tlgx = SelectTlgx(tlgxFiles, owner);
        if (tlgx == null)
            return new Result(false, "Konverzia zrušená (žiadny TLGX).", null);

        Directory.CreateDirectory(outputDir);

        // Cesty s '#' (#ROZPRACOVANE) XConverter často nezvládne → cez %TEMP%.
        bool useTemp = inputDir.Contains('#', StringComparison.Ordinal)
                       || outputDir.Contains('#', StringComparison.Ordinal);

        string runInput = inputDir;
        string runOutput = outputDir;
        string? tempRoot = null;
        try
        {
            if (useTemp)
            {
                tempRoot = Path.Combine(Path.GetTempPath(),
                    "CncPrieniky3D_xconv_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);
                foreach (var xcs in Directory.EnumerateFiles(inputDir, "*.xcs"))
                    File.Copy(xcs, Path.Combine(tempRoot, Path.GetFileName(xcs)), overwrite: true);
                runInput = tempRoot;
                runOutput = tempRoot;
            }

            string workDir = Path.GetDirectoryName(exe) ?? PrimarySearchRoot;
            // Ako v .ps1: -s -i "input" -t "tlgx" -o "output" -m 0
            string args =
                $"-s -i \"{runInput}\" -t \"{tlgx}\" -o \"{runOutput}\" -m 0";

            string logPath = Path.Combine(Path.GetTempPath(),
                $"XConverter_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            var run = RunProcess(exe, args, workDir, timeoutMs: 300_000);
            WriteLog(logPath, exe, runInput, tlgx, runOutput, args, run);

            if (useTemp && tempRoot != null && run.ExitCode == 0)
            {
                foreach (var pgmx in Directory.EnumerateFiles(tempRoot, "*.pgmx"))
                    File.Copy(pgmx, Path.Combine(outputDir, Path.GetFileName(pgmx)), overwrite: true);
            }

            if (run.ExitCode == 0)
            {
                int pgmxCount = Directory.EnumerateFiles(outputDir, "*.pgmx").Count();
                return new Result(true,
                    $"XConverter dokončený.\n\n" +
                    $"Vstup: {inputDir}\n" +
                    $"TLGX: {Path.GetFileName(tlgx)}\n" +
                    $"Výstup: {outputDir}\n" +
                    $".pgmx súborov: {pgmxCount}\n\n" +
                    $"Log: {logPath}",
                    logPath);
            }

            string detail = !string.IsNullOrWhiteSpace(run.StdErr)
                ? TrimMsg(run.StdErr)
                : TrimMsg(run.StdOut);
            return new Result(false,
                $"Chyba konverzie (exit {run.ExitCode}).\n\n" +
                (detail.Length > 0 ? detail + "\n\n" : "") +
                "Možné príčiny: neplatný .xcs, zlý TLGX, oprávnenia.\n\n" +
                $"Log: {logPath}",
                logPath);
        }
        catch (Exception ex)
        {
            return new Result(false, $"Chyba pri spustení XConverter: {ex.Message}", null);
        }
        finally
        {
            if (tempRoot != null)
            {
                try { Directory.Delete(tempRoot, recursive: true); }
                catch { /* ignore */ }
            }
        }
    }

    public static string? FindXConverter()
    {
        if (Directory.Exists(PrimarySearchRoot))
        {
            try
            {
                string? found = Directory.EnumerateFiles(PrimarySearchRoot, "XConverter.exe",
                        SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            catch { /* ignore access */ }
        }

        foreach (string path in KnownExePaths)
        {
            if (File.Exists(path))
                return path;
        }

        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                 })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                string? found = Directory.EnumerateFiles(root, "XConverter.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            catch { /* ignore */ }
        }

        return null;
    }

    public static List<string> FindTlgxFiles(string? alsoSearchPath = null)
    {
        var list = new List<string>();
        void AddFile(string path)
        {
            if (File.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                list.Add(path);
        }

        void AddFrom(string dir, bool recurse)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                var opt = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(dir, "*.tlgx", opt))
                    AddFile(f);
            }
            catch { /* ignore */ }
        }

        foreach (string known in KnownTlgxPaths)
            AddFile(known);

        AddFrom(PrimaryTlgxDir, recurse: false);
        AddFrom(@"C:\Program Files\SCM Group\Maestro\Tlgx", recurse: false);
        AddFrom(@"C:\SCM Group\Maestro\Tlgx", recurse: false);

        if (!string.IsNullOrWhiteSpace(alsoSearchPath))
            AddFrom(alsoSearchPath, recurse: true);

        // def.tlgx vždy hore v zozname
        list.Sort((a, b) =>
        {
            bool aDef = Path.GetFileName(a).Equals("def.tlgx", StringComparison.OrdinalIgnoreCase);
            bool bDef = Path.GetFileName(b).Equals("def.tlgx", StringComparison.OrdinalIgnoreCase);
            if (aDef != bDef) return aDef ? -1 : 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        return list;
    }

    private static string? SelectTlgx(IReadOnlyList<string> files, Window? owner)
    {
        // Jediný súbor, alebo existuje predvolené def.tlgx → bez dialógu
        if (files.Count == 1)
            return files[0];

        string? preferred = files.FirstOrDefault(f =>
            f.Equals(DefaultTlgxPath, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(f).Equals("def.tlgx", StringComparison.OrdinalIgnoreCase));
        if (preferred != null)
            return preferred;

        var dlg = new OpenFileDialog
        {
            Title = "Vyberte TLGX pre XConverter",
            Filter = "TLGX (*.tlgx)|*.tlgx|Všetky súbory (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = "def.tlgx"
        };

        if (Directory.Exists(PrimaryTlgxDir))
            dlg.InitialDirectory = PrimaryTlgxDir;
        else
        {
            string? dir = Path.GetDirectoryName(files[0]);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (owner != null)
            return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string exePath, string args, string workingDir, int timeoutMs)
    {
        // Ako v XConverter.ps1: Verb=runas → UAC / spustenie ako správca.
        // UseShellExecute=true je povinné pre runas (redirect stdout nejde).
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null)
                return (-1, "", "XConverter sa nepodarilo spustiť.");

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, "", "XConverter timeout.");
            }

            return (p.ExitCode, "", "");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Používateľ zrušil UAC
            return (-1, "", "Spustenie ako správca bolo zrušené (UAC).");
        }
    }

    private static void WriteLog(
        string logPath,
        string exe,
        string input,
        string tlgx,
        string output,
        string args,
        (int ExitCode, string StdOut, string StdErr) run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("XConverter Execution Log");
        sb.AppendLine("========================");
        sb.AppendLine($"Datum: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"XConverter: {exe}");
        sb.AppendLine($"Vstup: {input}");
        sb.AppendLine($"TLGX: {tlgx}");
        sb.AppendLine($"Vystup: {output}");
        sb.AppendLine($"Parametre: {args}");
        sb.AppendLine($"Exit Code: {run.ExitCode}");
        sb.AppendLine();
        sb.AppendLine("--- STANDARDNY VYSTUP ---");
        sb.AppendLine(run.StdOut);
        sb.AppendLine();
        sb.AppendLine("--- CHYBOVY VYSTUP ---");
        sb.AppendLine(run.StdErr);
        File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
    }

    private static string TrimMsg(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length > 500) s = s[..500] + "…";
        return s;
    }
}
