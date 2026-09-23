using System.IO;
using System.Text.Json;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace CncPrieniky3D.Models;

/// <summary>Používateľské farby 3D scény a UI — uložené do AppData.</summary>
public sealed class ViewColors
{
    public static ViewColors Current { get; } = LoadOrDefault();

    public MediaColor WindowBackground { get; set; } = FromRgb(0, 36, 81);    // #002451
    public MediaColor ToolbarBackground { get; set; } = FromRgb(0, 28, 64);   // #001C40
    public MediaColor ViewportBackground { get; set; } = FromRgb(0, 24, 51);  // #001833

    public MediaColor Panel { get; set; } = FromRgb(210, 180, 130);
    public MediaColor PanelSelected { get; set; } = FromRgb(120, 170, 220);
    public MediaColor Vertex { get; set; } = FromRgb(255, 200, 60);

    public MediaColor Koliky { get; set; } = FromRgb(255, 30, 30);
    public MediaColor Skrutky { get; set; } = FromRgb(220, 40, 40);
    public MediaColor CncZnacenie { get; set; } = FromRgb(0, 255, 80);
    public MediaColor CncZnaceniePreview { get; set; } = FromRgb(220, 25, 25);
    public MediaColor Diery { get; set; } = FromRgb(220, 25, 25);
    public MediaColor Abs { get; set; } = FromRgb(0, 110, 255);
    public MediaColor Dotyky { get; set; } = FromRgb(255, 70, 40);
    public MediaColor DotykOznaceny { get; set; } = FromRgb(0, 200, 120);
    public MediaColor DotykVybrany { get; set; } = FromRgb(255, 220, 40);

    public void ResetToDefaults()
    {
        var d = CreateDefaults();
        WindowBackground = d.WindowBackground;
        ToolbarBackground = d.ToolbarBackground;
        ViewportBackground = d.ViewportBackground;
        Panel = d.Panel;
        PanelSelected = d.PanelSelected;
        Vertex = d.Vertex;
        Koliky = d.Koliky;
        Skrutky = d.Skrutky;
        CncZnacenie = d.CncZnacenie;
        CncZnaceniePreview = d.CncZnaceniePreview;
        Diery = d.Diery;
        Abs = d.Abs;
        Dotyky = d.Dotyky;
        DotykOznaceny = d.DotykOznaceny;
        DotykVybrany = d.DotykVybrany;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var dto = ToDto();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(dto, JsonOpts));
        }
        catch
        {
            // ignore write errors
        }
    }

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CncPrieniky3D",
            "view-colors.json");

    private static ViewColors LoadOrDefault()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var dto = JsonSerializer.Deserialize<ColorsDto>(json, JsonOpts);
                if (dto != null)
                    return FromDto(dto);
            }
        }
        catch
        {
            // fall through
        }
        return CreateDefaults();
    }

    private static ViewColors CreateDefaults() => new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static MediaColor FromRgb(byte r, byte g, byte b) => MediaColor.FromRgb(r, g, b);

    private ColorsDto ToDto() => new()
    {
        WindowBackground = ToHex(WindowBackground),
        ToolbarBackground = ToHex(ToolbarBackground),
        ViewportBackground = ToHex(ViewportBackground),
        Panel = ToHex(Panel),
        PanelSelected = ToHex(PanelSelected),
        Vertex = ToHex(Vertex),
        Koliky = ToHex(Koliky),
        Skrutky = ToHex(Skrutky),
        CncZnacenie = ToHex(CncZnacenie),
        CncZnaceniePreview = ToHex(CncZnaceniePreview),
        Diery = ToHex(Diery),
        Abs = ToHex(Abs),
        Dotyky = ToHex(Dotyky),
        DotykOznaceny = ToHex(DotykOznaceny),
        DotykVybrany = ToHex(DotykVybrany),
    };

    private static ViewColors FromDto(ColorsDto d)
    {
        var c = CreateDefaults();
        if (TryParse(d.WindowBackground, out var w)) c.WindowBackground = w;
        if (TryParse(d.ToolbarBackground, out var tb)) c.ToolbarBackground = tb;
        if (TryParse(d.ViewportBackground, out var vb)) c.ViewportBackground = vb;
        if (TryParse(d.Panel, out var p)) c.Panel = p;
        if (TryParse(d.PanelSelected, out var ps)) c.PanelSelected = ps;
        if (TryParse(d.Vertex, out var v)) c.Vertex = v;
        if (TryParse(d.Koliky, out var k)) c.Koliky = k;
        if (TryParse(d.Skrutky, out var s)) c.Skrutky = s;
        if (TryParse(d.CncZnacenie, out var cz)) c.CncZnacenie = cz;
        if (TryParse(d.CncZnaceniePreview, out var czp)) c.CncZnaceniePreview = czp;
        if (TryParse(d.Diery, out var di)) c.Diery = di;
        if (TryParse(d.Abs, out var a)) c.Abs = a;
        if (TryParse(d.Dotyky, out var dt)) c.Dotyky = dt;
        if (TryParse(d.DotykOznaceny, out var dozn)) c.DotykOznaceny = dozn;
        if (TryParse(d.DotykVybrany, out var dvyb)) c.DotykVybrany = dvyb;
        return c;
    }

    public static string ToHex(MediaColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryParse(string? hex, out MediaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6) return false;
        try
        {
            byte r = Convert.ToByte(hex[..2], 16);
            byte g = Convert.ToByte(hex[2..4], 16);
            byte b = Convert.ToByte(hex[4..6], 16);
            color = MediaColor.FromRgb(r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class ColorsDto
    {
        public string? WindowBackground { get; set; }
        public string? ToolbarBackground { get; set; }
        public string? ViewportBackground { get; set; }
        public string? Panel { get; set; }
        public string? PanelSelected { get; set; }
        public string? Vertex { get; set; }
        public string? Koliky { get; set; }
        public string? Skrutky { get; set; }
        public string? CncZnacenie { get; set; }
        public string? CncZnaceniePreview { get; set; }
        public string? Diery { get; set; }
        public string? Abs { get; set; }
        public string? Dotyky { get; set; }
        public string? DotykOznaceny { get; set; }
        public string? DotykVybrany { get; set; }
    }
}
