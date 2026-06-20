using NUnit.Framework;
using UnityEngine;
using SFMap.Pipeline;

namespace SFMap.Tests
{
    public class GeoProjectionTests
    {
        // Castro/Noe Valley bounding box from map.osm
        static readonly OsmBounds SFBounds = new OsmBounds(
            minLat: 37.7336, maxLat: 37.7467,
            minLon: -122.4527, maxLon: -122.4304
        );

        [Test]
        public void Origin_CenterMatchesBounds()
        {
            var origin = new GeoOrigin(SFBounds.CenterLon, SFBounds.CenterLat);
            Assert.AreEqual(SFBounds.CenterLon, origin.CenterLon, 1e-10);
            Assert.AreEqual(SFBounds.CenterLat, origin.CenterLat, 1e-10);
        }

        [Test]
        public void ToWorldPoint_CenterMapsToZero()
        {
            var origin = new GeoOrigin(SFBounds.CenterLon, SFBounds.CenterLat);
            Vector3 result = GeoProjection.ToWorldPoint(SFBounds.CenterLon, SFBounds.CenterLat, origin);
            Assert.AreEqual(0f, result.x, 0.001f);
            Assert.AreEqual(0f, result.z, 0.001f);
            Assert.AreEqual(0f, result.y);
        }

        [Test]
        public void ToWorldPoint_EastIsPositiveX()
        {
            var origin = new GeoOrigin(SFBounds.CenterLon, SFBounds.CenterLat);
            Vector3 west = GeoProjection.ToWorldPoint(SFBounds.MinLon, SFBounds.CenterLat, origin);
            Vector3 east = GeoProjection.ToWorldPoint(SFBounds.MaxLon, SFBounds.CenterLat, origin);
            Assert.Less(west.x, 0f, "West should be negative X");
            Assert.Greater(east.x, 0f, "East should be positive X");
        }

        [Test]
        public void ToWorldPoint_NorthIsPositiveZ()
        {
            var origin = new GeoOrigin(SFBounds.CenterLon, SFBounds.CenterLat);
            Vector3 south = GeoProjection.ToWorldPoint(SFBounds.CenterLon, SFBounds.MinLat, origin);
            Vector3 north = GeoProjection.ToWorldPoint(SFBounds.CenterLon, SFBounds.MaxLat, origin);
            Assert.Less(south.z, 0f, "South should be negative Z");
            Assert.Greater(north.z, 0f, "North should be positive Z");
        }

        [Test]
        public void ToWorldPoint_ExtentMatchesExpectedMeters()
        {
            // OSM bounds ~1.1km E-W × ~1.4km N-S at SF latitude
            var origin = new GeoOrigin(SFBounds.CenterLon, SFBounds.CenterLat);
            Vector3 sw = GeoProjection.ToWorldPoint(SFBounds.MinLon, SFBounds.MinLat, origin);
            Vector3 ne = GeoProjection.ToWorldPoint(SFBounds.MaxLon, SFBounds.MaxLat, origin);
            float widthM = ne.x - sw.x;
            float depthM = ne.z - sw.z;
            Assert.Greater(widthM, 900f,  "E-W extent should exceed 900m");
            Assert.Less(widthM,    1500f, "E-W extent should be under 1500m");
            Assert.Greater(depthM, 1200f, "N-S extent should exceed 1200m");
            Assert.Less(depthM,    1700f, "N-S extent should be under 1700m");
        }

        [Test]
        public void WorldBounds_HasPositiveDimensions()
        {
            Rect bounds = GeoProjection.WorldBounds(SFBounds);
            Assert.Greater(bounds.width, 0f);
            Assert.Greater(bounds.height, 0f);
        }

        [Test]
        public void WorldBounds_CenteredNearOrigin()
        {
            Rect bounds = GeoProjection.WorldBounds(SFBounds);
            Assert.AreEqual(0f, bounds.center.x, 1f, "Rect center X should be ~0");
            Assert.AreEqual(0f, bounds.center.y, 1f, "Rect center Y should be ~0");
        }

        [Test]
        public void Initialize_StaticOriginProjectsCenterToZero()
        {
            GeoProjection.Initialize(SFBounds);
            Vector3 center = GeoProjection.ToWorldPoint(SFBounds.CenterLon, SFBounds.CenterLat);
            Assert.AreEqual(0f, center.x, 0.001f);
            Assert.AreEqual(0f, center.z, 0.001f);
        }

        [Test]
        public void MetersPerDegLon_LessThanMetersPerDegLat_AtSFLatitude()
        {
            // At ~37.7°N, longitudinal degrees span fewer meters than latitudinal
            var origin = new GeoOrigin(SFBounds.CenterLon, SFBounds.CenterLat);
            Assert.Less(origin.MetersPerDegLon, origin.MetersPerDegLat);
        }

        [Test]
        public void BoundsFromOSM_MatchesWorldBounds()
        {
            Rect a = GeoProjection.WorldBounds(SFBounds);
            Rect b = GeoProjection.BoundsFromOSM(SFBounds);
            Assert.AreEqual(a.x, b.x, 0.001f);
            Assert.AreEqual(a.y, b.y, 0.001f);
            Assert.AreEqual(a.width, b.width, 0.001f);
            Assert.AreEqual(a.height, b.height, 0.001f);
        }
    }
}
