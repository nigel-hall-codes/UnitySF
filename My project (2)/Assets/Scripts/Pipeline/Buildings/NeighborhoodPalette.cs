using System;
using UnityEngine;

namespace SFMap.Pipeline.Buildings
{
    /// <summary>One material role's colour options within a neighborhood palette.</summary>
    [Serializable]
    public struct RolePalette
    {
        public MaterialRole role;
        public Color[] colors;     // candidate colours (Pick/Constant) or ramp stops (Lerp)
        public PaletteMode mode;
    }

    /// <summary>
    /// A neighborhood's constrained colour palette (design #266 data-model.md §3), generated
    /// from a <c>PaletteDef</c> JSON by the importer. <see cref="Resolve"/> turns a
    /// (role, seed) pair into one deterministic colour so a re-import is byte-stable and the
    /// same building always gets the same colours (the determinism contract, data-model §6).
    /// Unity owns colour choice — authored parts carry only roles.
    /// </summary>
    [CreateAssetMenu(menuName = "SFMap/Neighborhood Palette", fileName = "NeighborhoodPalette")]
    public sealed class NeighborhoodPalette : ScriptableObject
    {
        public string neighborhood;
        public RolePalette[] roles;

        /// <summary>Deterministic colour for <paramref name="role"/> seeded by
        /// <paramref name="seed"/> (the assembler passes <c>hash(osm_id, role)</c>).
        /// <c>Pick</c> selects one colour by the seed; <c>Lerp</c> interpolates across the
        /// ramp by a seeded fraction; <c>Constant</c> always returns the first. Falls back to
        /// magenta when the role is absent or has no colours, so a gap is visible, not silent.</summary>
        public Color Resolve(MaterialRole role, uint seed)
        {
            if (roles != null)
            {
                for (int i = 0; i < roles.Length; i++)
                {
                    if (roles[i].role != role) continue;
                    var rp = roles[i];
                    if (rp.colors == null || rp.colors.Length == 0) break;
                    switch (rp.mode)
                    {
                        case PaletteMode.Constant:
                            return rp.colors[0];
                        case PaletteMode.Pick:
                            return rp.colors[(int)(seed % (uint)rp.colors.Length)];
                        case PaletteMode.Lerp:
                            if (rp.colors.Length == 1) return rp.colors[0];
                            // Seeded fraction across the ramp; span scales to the stop count.
                            float t = (seed & 0xFFFFu) / 65535f;
                            float scaled = t * (rp.colors.Length - 1);
                            int lo = Mathf.Clamp((int)scaled, 0, rp.colors.Length - 2);
                            return Color.Lerp(rp.colors[lo], rp.colors[lo + 1], scaled - lo);
                    }
                }
            }
            return Color.magenta;
        }
    }
}
