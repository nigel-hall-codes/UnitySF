using UnityEngine;
using SFMap.Pipeline.Buildings.Gen;

namespace SFMap.Pipeline.Buildings
{
    /// <summary>
    /// A reusable architectural part (window, door, sign, …), described entirely by <b>which
    /// generator builds it and with what numbers</b> — no authored geometry (design #452 D2, #454).
    /// <c>BuildingTemplateLibraryImporter</c> converts a <c>PartDef</c> JSON into one of these; the
    /// assembler resolves <see cref="generatorId"/> to an <see cref="IPartGenerator"/>, runs it
    /// through <see cref="PartMeshCache"/>, and instances the resulting shared mesh onto a
    /// building's facade.
    /// <para>The material role of each submesh is <b>emitted by the generator</b>, not authored
    /// here: role tagging can no longer drift from the geometry it describes.</para>
    /// </summary>
    [CreateAssetMenu(menuName = "SFMap/Building Part", fileName = "BuildingPart")]
    public sealed class BuildingPart : ScriptableObject
    {
        [Tooltip("Stable part id, e.g. \"window_sunset_2x3\" — referenced by templates.")]
        public string id;

        public PartCategory category;

        [Tooltip("Which IPartGenerator builds this part, e.g. \"window.double_hung\". Empty or " +
                 "unknown → the part is skipped with a warning; there is no fallback geometry.")]
        public string generatorId;

        [Tooltip("The generator's parameter block — a flat, name-keyed bag (see PartParams).")]
        public PartParams parameters;

        [Tooltip("Authored real-world size in metres (w, h, d). Informational: the generated mesh " +
                 "carries its own size, taken from `parameters`.")]
        public Vector3 sizeMeters;

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
