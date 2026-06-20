using UnityEngine;

namespace SFMap.Pipeline
{
    public readonly struct GeoOrigin
    {
        public readonly double CenterLon;
        public readonly double CenterLat;
        public readonly float MetersPerDegLon;
        public readonly float MetersPerDegLat;

        public GeoOrigin(double centerLon, double centerLat)
        {
            CenterLon = centerLon;
            CenterLat = centerLat;
            MetersPerDegLat = 111320f;
            MetersPerDegLon = (float)(System.Math.Cos(centerLat * System.Math.PI / 180.0) * 111320.0);
        }
    }

    public readonly struct OsmBounds
    {
        public readonly double MinLat, MaxLat, MinLon, MaxLon;

        public OsmBounds(double minLat, double maxLat, double minLon, double maxLon)
        {
            MinLat = minLat; MaxLat = maxLat;
            MinLon = minLon; MaxLon = maxLon;
        }

        public double CenterLat => (MinLat + MaxLat) / 2.0;
        public double CenterLon => (MinLon + MaxLon) / 2.0;
    }

    public static class GeoProjection
    {
        public static GeoOrigin Origin { get; private set; }

        public static void Initialize(OsmBounds bounds)
        {
            Origin = new GeoOrigin(bounds.CenterLon, bounds.CenterLat);
        }

        public static Vector3 ToWorldPoint(double lon, double lat, GeoOrigin origin)
        {
            float x = (float)((lon - origin.CenterLon) * origin.MetersPerDegLon);
            float z = (float)((lat - origin.CenterLat) * origin.MetersPerDegLat);
            return new Vector3(x, 0f, z);
        }

        public static Vector3 ToWorldPoint(double lon, double lat)
            => ToWorldPoint(lon, lat, Origin);

        public static Rect WorldBounds(OsmBounds bounds)
        {
            var origin = new GeoOrigin(bounds.CenterLon, bounds.CenterLat);
            Vector3 sw = ToWorldPoint(bounds.MinLon, bounds.MinLat, origin);
            Vector3 ne = ToWorldPoint(bounds.MaxLon, bounds.MaxLat, origin);
            return new Rect(sw.x, sw.z, ne.x - sw.x, ne.z - sw.z);
        }

        public static Rect BoundsFromOSM(OsmBounds bounds) => WorldBounds(bounds);
    }
}
