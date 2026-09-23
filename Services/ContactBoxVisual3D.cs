using System.Windows.Media.Media3D;
using CncPrieniky3D.Models;
using HelixToolkit.Wpf;

namespace CncPrieniky3D.Services;

/// <summary>Kváder styčnej plochy s odkazom na ContactMark (klik v 3D).</summary>
internal sealed class ContactBoxVisual3D : BoxVisual3D
{
    public ContactMark? Contact { get; set; }
}
