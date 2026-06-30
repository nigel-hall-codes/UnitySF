using System;

namespace SFMap.Pipeline.Buildings
{
    // Wire DTOs for the server-exported library JSON (design #266 data-model.md §2),
    // parsed with UnityEngine.JsonUtility by BuildingTemplateLibraryImporter.
    //
    // JsonUtility cannot deserialize string-keyed maps, so every place §2 specifies a
    // JSON object map this uses a JsonUtility-friendly array of explicit pairs instead:
    //   roleSubmeshes {"0":"Base"}        -> [{ "submesh":0, "role":"Base" }]
    //   exact[].roles {"Base":"Accent1"}  -> [{ "from":"Base", "to":"Accent1" }]
    //   palette roles {"Base":{...}}      -> [{ "role":"Base", "colors":[...], "mode":"pick" }]
    // This is a wire-shape refinement of §2 (semantically identical); the authoring
    // server (#274) must emit the same array form. Captured as a §2 reconciliation note.

    [Serializable]
    public sealed class LibraryManifestJson
    {
        public int version;
        public string exportedAt;
        public string[] neighborhoods;
    }

    // ---- Parts ----------------------------------------------------------------

    [Serializable]
    public sealed class PartDefJson
    {
        public string id;
        public string category;            // PartCategory name
        public string glb;                 // library-relative path to the GLB
        public SizeJson size_m;
        public RoleSubmeshJson[] roleSubmeshes;
        public string anchor;
        public float mountDepth_m;
        public int version;
    }

    [Serializable] public struct SizeJson { public float w; public float h; public float d; }
    [Serializable] public struct RoleSubmeshJson { public int submesh; public string role; }

    // ---- Templates ------------------------------------------------------------

    [Serializable]
    public sealed class TemplateDefJson
    {
        public string id;
        public string displayName;
        public CompatibilityJson compatibility;
        public ExactJson[] exact;
        public RuleJson[] rules;
        public string[] roofParts;         // part ids
        public int version;
    }

    [Serializable]
    public sealed class CompatibilityJson
    {
        public string[] neighborhoods;
        public string[] building_types;
        public string[] footprint_shapes;
        public RangeJson width_m;
        public RangeJson depth_m;
        public IntRangeJson floor_count;
    }

    [Serializable] public struct RangeJson { public float min; public float max; }
    [Serializable] public struct IntRangeJson { public int min; public int max; }

    [Serializable]
    public sealed class ExactJson
    {
        public string part;
        public string facade;
        public int floor;
        public float x;
        public float y;
        public float scale;
        public float rotation;
        public RolePairJson[] roles;
    }

    [Serializable] public struct RolePairJson { public string from; public string to; }

    [Serializable]
    public sealed class RuleJson
    {
        public string part;
        public string facade;
        public IntRangeJson floorRange;
        public float[] span;
        public RepeatJson repeat;
        public float probability;
        public ConstraintsJson constraints;
        public JitterJson jitter;
        public string[] variants;
    }

    [Serializable] public struct RepeatJson { public float spacingMeters; public int countMin; public int countMax; }
    [Serializable] public struct ConstraintsJson { public float minSpacingMeters; public float edgeMargin; public bool alignToFloorLine; public bool avoidExact; }
    [Serializable] public struct JitterJson { public float x; public float[] scale; public float rotation; }

    // ---- Palettes -------------------------------------------------------------

    [Serializable]
    public sealed class PaletteDefJson
    {
        public string neighborhood;
        public RoleDefJson[] roles;
        public int version;
    }

    [Serializable]
    public sealed class RoleDefJson
    {
        public string role;
        public string[] colors;     // "#RRGGBB" entries (Pick/Constant, or Lerp stops)
        public string[] ramp;       // optional explicit ramp stops for Lerp; falls back to colors
        public string mode;         // "pick" | "lerp" | "constant"
    }
}
