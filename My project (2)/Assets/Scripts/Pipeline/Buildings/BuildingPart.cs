using UnityEngine;

namespace SFMap.Pipeline.Buildings
{
    /// <summary>
    /// A reusable authored architectural part (window, door, sign, …), generated from a
    /// <c>PartDef</c> JSON + GLB by <c>BuildingTemplateLibraryImporter</c> (design #266
    /// data-model.md §3). The assembler (#270) instantiates <see cref="prefab"/> onto a
    /// building's mass, mapping each submesh's <see cref="submeshRoles"/> entry to a
    /// concrete colour via the neighborhood palette (or, for a sign, a PNG texture).
    /// </summary>
    [CreateAssetMenu(menuName = "SFMap/Building Part", fileName = "BuildingPart")]
    public sealed class BuildingPart : ScriptableObject
    {
        [Tooltip("Stable part id, e.g. \"window_sunset_2x3\" — referenced by templates.")]
        public string id;

        public PartCategory category;

        [Tooltip("Imported GLB geometry. Null until a glTF importer (glTFast) makes the GLB " +
                 "loadable as a GameObject — the importer warns and leaves this null otherwise.")]
        public GameObject prefab;

        [Tooltip("Authored real-world size in metres (w, h, d).")]
        public Vector3 sizeMeters;

        [Tooltip("Material role per prefab submesh, index-aligned to the prefab's submeshes.")]
        public MaterialRole[] submeshRoles;

        [Tooltip("How the part's normalized placement maps to its mesh origin (e.g. BottomCenter).")]
        public string anchor;

        [Tooltip("Signed offset along the wall normal: <0 insets (window), >0 protrudes (bay " +
                 "window). The assembler (#270) uses it to prevent z-fighting (design #266 D4).")]
        public float mountDepthMeters;

        [Tooltip("Sign parts get an authored/AI PNG texture instead of a role colour, and " +
                 "stay a separate (non-combinable) material — the batching break (design #266).")]
        public bool isSign;
    }
}
