using System;
using UnityEngine;

namespace SFMap.Pipeline.Buildings
{
    [Serializable]
    public struct FloatRange
    {
        public float min;
        public float max;
        public bool Contains(float v) => v >= min && v <= max;
    }

    [Serializable]
    public struct IntRange
    {
        public int min;
        public int max;
        public bool Contains(int v) => v >= min && v <= max;
    }

    /// <summary>Which buildings a template may dress (data-model.md §2 TemplateDef.compatibility).
    /// An empty list/array field means "no constraint on this axis".</summary>
    [Serializable]
    public sealed class Compatibility
    {
        public string[] neighborhoods;
        public string[] buildingTypes;
        public string[] footprintShapes;
        public FloatRange widthM;
        public FloatRange depthM;
        public IntRange floorCount;

        /// <summary>True when this template admits a building with the given classification facts
        /// (from the bake sidecar #268). Each axis passes when unconstrained or when it contains
        /// the building's value.</summary>
        public bool Admits(string neighborhood, string buildingType, string footprintShape,
                           float widthMeters, float depthMeters, int floors)
        {
            return AxisAdmits(neighborhoods, neighborhood)
                && AxisAdmits(buildingTypes, buildingType)
                && AxisAdmits(footprintShapes, footprintShape)
                && widthM.Contains(widthMeters)
                && depthM.Contains(depthMeters)
                && floorCount.Contains(floors);
        }

        private static bool AxisAdmits(string[] allowed, string value)
        {
            if (allowed == null || allowed.Length == 0) return true;
            for (int i = 0; i < allowed.Length; i++)
                if (allowed[i] == value) return true;
            return false;
        }
    }

    /// <summary>Maps a part's authored role to the building's role (data-model.md §Placement).</summary>
    [Serializable]
    public struct RoleMap
    {
        public MaterialRole from;
        public MaterialRole to;
    }

    /// <summary>One Exact-Layout placement — the fixed bones of a style (data-model.md §2).
    /// Reproduced identically every time; only the normalized coords stretch to the real facade.
    /// <see cref="part"/> is a part id resolved against the library by the assembler (#270).</summary>
    [Serializable]
    public sealed class ExactPlacement
    {
        public string part;
        public Facade facade;
        public int floor;
        public float x;
        public float y;
        public float scale;
        public float rotation;
        public RoleMap[] roles;
    }

    [Serializable]
    public struct Repeat
    {
        public float spacingMeters;
        public int countMin;
        public int countMax;
    }

    [Serializable]
    public struct PlacementConstraints
    {
        /// <summary>A floor under the rule's repeat pitch (<c>spacing = max(spacingMeters,
        /// minSpacingMeters, 0.1)</c>), and so a ceiling on its slot count.
        /// <para>It used to double as the exclusion radius around an Exact placement, which is
        /// what made the radius uncrossable past the repeat pitch (#491). Since the occupancy
        /// table compares the parts' real extents, this no longer sets any exclusion.</para></summary>
        public float minSpacingMeters;
        public float edgeMargin;
        public bool alignToFloorLine;
        /// <summary>Retained for schema compatibility and still asserted by
        /// <c>TemplateWiringTests</c>, but no longer a gate: every procedural placement consults
        /// the shared occupancy table (#491), because a rule that would place inside another
        /// artifact's shell has nothing to gain from doing so.</summary>
        public bool avoidExact;
    }

    [Serializable]
    public struct Jitter
    {
        public float x;
        public float[] scale;   // [min, max] multiplier
        public float rotation;
    }

    /// <summary>One Procedural-Rule placement — the engine of believable variety
    /// (data-model.md §2). The assembler (#270) derives a slot count from the rule's span and
    /// the building's real dimensions, seeded by <c>hash(osm_id, ruleIndex, slotIndex)</c>.</summary>
    [Serializable]
    public sealed class ProceduralRule
    {
        public string part;
        public Facade facade;
        public IntRange floorRange;
        public float[] span;        // [x0, x1] normalized horizontal span
        public Repeat repeat;
        public float probability;
        public PlacementConstraints constraints;
        public Jitter jitter;
        public string[] variants;   // part-id variants picked per slot

        /// <summary>Size the part to the facade instead of leaving it at its authored width
        /// (#487). The placement overwrites <see cref="stretchParam"/> in the part's parameter bag
        /// with the rule's span in metres — <c>(x1 − x0) · facadeLength</c>, after
        /// <c>edgeMargin</c> — before the generator runs, so the part is <i>rebuilt</i> at that
        /// width rather than scaled to it.
        ///
        /// <para><b>Why not <c>scale</c>.</b> <c>PlacePart</c>'s scale is uniform, so widening a
        /// 7.2 m shopfront to 20 m would also make its bulkhead and mullions 2.8× taller and
        /// thicker. A wider shopfront wants more bays, not fatter trim — which is what
        /// <c>StorefrontGenerator</c> already does when it is told the width, since it derives bay
        /// count from <c>bayPitch</c>. The placement layer needs to know nothing about storefronts
        /// to say this: the parameter block is a flat name-keyed bag (#454), so overwriting one
        /// entry is family-agnostic.</para>
        ///
        /// <para>Only meaningful for a rule that places one part across its span
        /// (<c>countMax = 1</c>); with several slots each would be handed the whole span.</para></summary>
        public bool stretchToFacade;

        /// <summary>Which parameter <see cref="stretchToFacade"/> overwrites. Empty →
        /// <see cref="DefaultStretchParam"/>. Authorable rather than hard-wired so a family whose
        /// span-wise dimension is not called <c>w</c> can still opt in.</summary>
        public string stretchParam;

        /// <summary>The width parameter every family shipped so far uses.</summary>
        public const string DefaultStretchParam = "w";

        /// <summary>The parameter name this rule stretches, resolved.</summary>
        public string StretchParamName =>
            string.IsNullOrEmpty(stretchParam) ? DefaultStretchParam : stretchParam;
    }

    /// <summary>
    /// A building style: a recipe of placement directives the assembler (#270) resolves against a
    /// building's real dimensions and facades (design #266 data-model.md §3). Generated from a
    /// <c>TemplateDef</c> JSON by the importer; part ids in <see cref="exact"/>/<see cref="rules"/>
    /// are resolved against the library at assembly time, while <see cref="roofParts"/> are direct
    /// <see cref="BuildingPart"/> references resolved at import.
    /// </summary>
    [CreateAssetMenu(menuName = "SFMap/Building Template", fileName = "BuildingTemplate")]
    public sealed class BuildingTemplate : ScriptableObject
    {
        public string id;
        public string displayName;
        public Compatibility compatibility;
        public ExactPlacement[] exact;
        public ProceduralRule[] rules;
        public BuildingPart[] roofParts;
    }
}
