using System;
using UnityEngine;

namespace SFMap.Pipeline.Buildings
{
    /// <summary>One template's authored weight within a neighborhood's district weighting.</summary>
    [Serializable]
    public struct TemplateWeight
    {
        public string templateId;
        public float weight;
    }

    /// <summary>
    /// A neighborhood's district-authored template selection weights (design #326 D4/#343),
    /// generated from <c>library.json</c>'s <c>districtTemplateWeights[]</c> by the importer.
    /// Consumed by <c>BuildingAssembler.TryMatch</c> to make the compatible-template tie-break
    /// weight-aware instead of uniform, while staying deterministic (seeded by osm_id, same as
    /// the rest of the placement model).
    /// </summary>
    [CreateAssetMenu(menuName = "SFMap/Neighborhood Template Weights", fileName = "NeighborhoodTemplateWeights")]
    public sealed class NeighborhoodTemplateWeights : ScriptableObject
    {
        public string neighborhood;
        public TemplateWeight[] weights;

        /// <summary>Weight for <paramref name="templateId"/>, or <paramref name="defaultWeight"/>
        /// when this neighborhood's district didn't author one — an unlisted compatible template
        /// still competes, so authoring a district doesn't silently exclude templates its author
        /// never mentioned.
        ///
        /// <para><b>An authored 0 means zero</b> (#475/#492). It previously meant "unset" and fell
        /// back to <paramref name="defaultWeight"/>, which left a district with no way to
        /// <i>exclude</i> a template — the concrete cost being <c>trivial_window</c>, the bundled
        /// MVP sample, whose empty compatibility admits every building in the city and which
        /// weights could therefore only thin (to ~1 building in 14) rather than switch off. Listing
        /// a template at 0 is now the exclusion. Negative weights are clamped to 0 rather than
        /// treated as unset, so an authoring slip cannot resurrect a template.</para>
        ///
        /// <para>Note this makes <i>presence in the list</i> the signal, and JsonUtility maps a
        /// missing <c>"weight"</c> field to 0 — so a row that omits the weight now excludes instead
        /// of defaulting. BuildingTemplateLibraryImporter logs every zero it imports for exactly
        /// that reason. BuildingAssembler.PickWeighted already falls back to a uniform pick when the
        /// weights total 0, so a district that zeroes everything degrades rather than divides by
        /// zero.</para></summary>
        public float WeightFor(string templateId, float defaultWeight)
        {
            if (weights != null)
                for (int i = 0; i < weights.Length; i++)
                    if (weights[i].templateId == templateId)
                        return weights[i].weight > 0f ? weights[i].weight : 0f;
            return defaultWeight;
        }
    }
}
