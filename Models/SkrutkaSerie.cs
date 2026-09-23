namespace CncPrieniky3D.Models;

/// <summary>Jedna séria skrutiek na styčnej ploche (stred plochy, ako kolíky).</summary>
public sealed class SkrutkaSerie
{
    public double OdPredu { get; set; }
    public int PocetSkrutiek { get; set; }
    public double RoztecSkrutiek { get; set; }

    /// <summary>Ak true, „Od predu“ sa meria od opačnej hrany plochy.</summary>
    public bool ZDruhejStrany { get; set; }

    /// <summary>
    /// Symetrické: len Od predu + počet; rozteč = (dĺžka − 2·Od predu) / (počet − 1).
    /// </summary>
    public bool Symetricke { get; set; }

    /// <summary>
    /// Pri Symetrické: true = dĺžka medzi 1. a posledným kolíkom (Od predu od 1. kolíka);
    /// false = dĺžka celého dotyku (Od predu od hrany dotyku).
    /// </summary>
    public bool SymetriaMedziKolikmi { get; set; }

    public int Cislo { get; set; }
}
