namespace CncPrieniky3D.Models;

/// <summary>Jedna séria kolíkov na styčnej ploche.</summary>
public sealed class KolikSerie
{
    public double OdPredu { get; set; }
    public int PocetKolikov { get; set; }
    public double RoztecKolikov { get; set; }

    /// <summary>Ak true, „Od predu“ sa meria od opačnej hrany plochy.</summary>
    public bool ZDruhejStrany { get; set; }

    /// <summary>
    /// Kolíky súmerné okolo stredu plochy: 1. = stred − ((n−1)/2)·rozteč, ďalej +rozteč.
    /// Od predu / z druhej strany sa nepoužívajú.
    /// </summary>
    public bool ZoStredu { get; set; }

    public int Cislo { get; set; }
}
