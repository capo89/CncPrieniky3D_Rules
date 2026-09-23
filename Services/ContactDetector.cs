using System.Windows.Media.Media3D;
using CncPrieniky3D.Models;

namespace CncPrieniky3D.Services;

/// <summary>
/// Nájde styčné plochy medzi AABB dielov v WCS (diel voči dielu).
/// </summary>
internal static class ContactDetector
{
    private const double GapTol = 1.5;
    private const double OverlapTol = 1.5;
    private const double MinArea = 50.0;

    private readonly struct Box
    {
        public Box(DielecModel d)
        {
            Name = d.Nazov;
            Min = new Point3D(d.WcsMinX, d.WcsMinY, d.WcsMinZ);
            Max = new Point3D(
                d.WcsMinX + Math.Max(d.RozmerX, 0.1),
                d.WcsMinY + Math.Max(d.RozmerY, 0.1),
                d.WcsMinZ + Math.Max(d.RozmerZ, 0.1));
        }

        public string Name { get; }
        public Point3D Min { get; }
        public Point3D Max { get; }
    }

    public static List<ContactMark> Find(IReadOnlyList<DielecModel> diely)
    {
        var boxes = diely.Select(d => new Box(d)).ToList();
        var result = new List<ContactMark>();

        for (int i = 0; i < boxes.Count; i++)
        {
            for (int j = i + 1; j < boxes.Count; j++)
                result.AddRange(FindPair(boxes[i], boxes[j]));
        }

        int cislo = 1;
        foreach (var c in result)
            c.Cislo = cislo++;

        return result;
    }

    private static IEnumerable<ContactMark> FindPair(Box a, Box b)
    {
        double ox0 = Math.Max(a.Min.X, b.Min.X);
        double ox1 = Math.Min(a.Max.X, b.Max.X);
        double oy0 = Math.Max(a.Min.Y, b.Min.Y);
        double oy1 = Math.Min(a.Max.Y, b.Max.Y);
        double oz0 = Math.Max(a.Min.Z, b.Min.Z);
        double oz1 = Math.Min(a.Max.Z, b.Max.Z);

        double ox = ox1 - ox0;
        double oy = oy1 - oy0;
        double oz = oz1 - oz0;

        if (oy > 1.0 && oz > 1.0)
        {
            var hit = TryAxisContact(a, b, 0, a.Max.X, a.Min.X, b.Max.X, b.Min.X, oy0, oy1, oz0, oz1);
            if (hit != null) yield return hit;
        }

        if (ox > 1.0 && oz > 1.0)
        {
            var hit = TryAxisContact(a, b, 1, a.Max.Y, a.Min.Y, b.Max.Y, b.Min.Y, ox0, ox1, oz0, oz1);
            if (hit != null) yield return hit;
        }

        if (ox > 1.0 && oy > 1.0)
        {
            var hit = TryAxisContact(a, b, 2, a.Max.Z, a.Min.Z, b.Max.Z, b.Min.Z, ox0, ox1, oy0, oy1);
            if (hit != null) yield return hit;
        }
    }

    private static ContactMark? TryAxisContact(
        Box a, Box b, int axis,
        double aFaceMax, double aFaceMin,
        double bFaceMax, double bFaceMin,
        double u0, double u1, double v0, double v1)
    {
        double area = (u1 - u0) * (v1 - v0);
        if (area < MinArea)
            return null;

        double gapAb = bFaceMin - aFaceMax;
        double gapBa = aFaceMin - bFaceMax;

        double plane = 0;
        bool touch = false;
        if (gapAb >= -OverlapTol && gapAb <= GapTol)
        {
            plane = (aFaceMax + bFaceMin) * 0.5;
            touch = true;
        }
        else if (gapBa >= -OverlapTol && gapBa <= GapTol)
        {
            plane = (bFaceMax + aFaceMin) * 0.5;
            touch = true;
        }

        if (!touch)
            return null;

        const double thickness = 6.0; // hrubšie kvôli klikaniu v 3D
        Point3D center;
        Vector3D size;

        if (axis == 0)
        {
            center = new Point3D(plane, (u0 + u1) * 0.5, (v0 + v1) * 0.5);
            size = new Vector3D(thickness, u1 - u0, v1 - v0);
        }
        else if (axis == 1)
        {
            center = new Point3D((u0 + u1) * 0.5, plane, (v0 + v1) * 0.5);
            size = new Vector3D(u1 - u0, thickness, v1 - v0);
        }
        else
        {
            center = new Point3D((u0 + u1) * 0.5, (v0 + v1) * 0.5, plane);
            size = new Vector3D(u1 - u0, v1 - v0, thickness);
        }

        return new ContactMark
        {
            PartA = a.Name,
            PartB = b.Name,
            Center = center,
            Size = size,
            Axis = axis,
            Oznacenie = ""
        };
    }
}
