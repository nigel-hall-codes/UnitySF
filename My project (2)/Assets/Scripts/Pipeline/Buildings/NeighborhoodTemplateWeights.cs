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
        /// when this neighborhood's district didn't author one (an unlisted compatible template
        /// still competes, just at the default weight rather than being excluded). An authored
        /// weight of 0 (or negative) is likewise treated as "not specified" here, matching
        /// ProceduralRule.probability's "&lt;= 0 means unset" convention elsewhere in this
        /// pipeline (BuildingAssembler.PlaceProcedural) — there is currently no way to author a
        /// hard exclusion via weight; that would need a distinct signal if ever needed.</summary>
        public float WeightFor(string templateId, float defaultWeight)
        {
            if (weights != null)
                for (int i = 0; i < weights.Length; i++)
                    if (weights[i].templateId == templateId)
                        return weights[i].weight > 0f ? weights[i].weight : defaultWeight;
            return defaultWeight;
        }
    }
}
