using System.IO;
using UnityEngine;

namespace SFMap.Pipeline.Buildings
{
    /// <summary>
    /// One place for the <c>Assets/SFBuildingTemplates/</c> library paths, shared by the library
    /// importer, the building assembler, and the decal importer (#430) so the location is defined once.
    /// </summary>
    public static class SFBuildingTemplatePaths
    {
        public const string LibraryRoot       = "Assets/SFBuildingTemplates";
        public const string OverridesAssetPath = LibraryRoot + "/Overrides";

        /// <summary>Absolute filesystem path of the Overrides/ dir (Application.dataPath is
        /// <c>&lt;project&gt;/Assets</c>).</summary>
        public static string OverridesAbsolute => Path.Combine(Application.dataPath, "SFBuildingTemplates/Overrides");
    }

    /// <summary>Facade geometry shared by part placement, decals, and backdrops (#430).</summary>
    public static class FacadeFrame
    {
        /// <summary>
        /// Outward (street-facing) horizontal normal for a facade, from its sidecar bearing
        /// (degrees CW from +Z). Winding-independent, so the facade faces the street regardless of
        /// OSM footprint orientation. Used by BuildingAssembler part placement, the decal importer,
        /// and the backdrop renderer so all three agree on which way the wall faces.
        /// </summary>
        public static Vector3 OutwardNormal(float bearingDeg)
        {
            float br = bearingDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(br), 0f, Mathf.Cos(br));
        }
    }
}
