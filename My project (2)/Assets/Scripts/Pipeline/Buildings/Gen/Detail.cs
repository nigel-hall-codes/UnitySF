using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// How every artifact family spends its <see cref="DetailLevel"/> budget (design #452
    /// generators.md §5.2). Three helpers, one implementation each — they were copy-pasted into six
    /// family generators, which is how the <see cref="Sectioned"/> bug below shipped five times
    /// before anyone noticed (#480).
    /// <para><b>The rule the whole file exists to enforce: a budget must never make something more
    /// expensive.</b> Every helper here is monotone in <see cref="DetailLevel"/> — Flat ≤ Reduced ≤
    /// Full in triangles, for any parameters. <c>EveryShippedPresetGetsCheaperAsDetailDrops</c> in
    /// <c>PartGeneratorSeamTests</c> asserts it across every preset in
    /// <c>SFBuildingTemplates/Parts</c>.</para>
    /// </summary>
    public static class Detail
    {
        /// <summary>
        /// The cross-section a moulding is swept with at this budget. <see cref="DetailLevel.Full"/>
        /// keeps the authored moulding; below it a 5–7 point Ogee/Sill/Bullnose is bevelled down to
        /// the 3-point <see cref="ProfileId.Chamfer"/>.
        /// <para><b>Deviation from generators.md §5.2</b>, which degrades swept profiles to "Box with
        /// bevel". Against the kernels as they actually merged that is a <i>regression</i>: a
        /// bevelled <see cref="Kernels.Box"/> emits its chamfer bands and corner triangles, so one
        /// costs ~30 triangles where a whole Ogee casing sweep costs 48 and a Chamfer sweep of the
        /// same run costs 16. Degrading the profile rather than the kernel keeps one code path and
        /// is what actually gets cheaper.</para>
        /// <para><b>Guarded by point count</b> — the #480 fix. Substituting Chamfer
        /// <i>unconditionally</i> upgrades a 2-point <see cref="ProfileId.Flat"/> band to 3 points,
        /// so a "cheaper" level emits more triangles than the one above it. Measured on the door
        /// family: the flush Sunset door's steel threshold went from 2 triangles to 6 and its whole
        /// <c>Reduced</c> mesh came out larger than its <c>Full</c> one (#470). A section already at
        /// or below Chamfer's cost is therefore left alone.</para>
        /// </summary>
        public static ProfileId Sectioned(ProfileId id, DetailLevel detail)
        {
            if (detail == DetailLevel.Full || id == ProfileId.None) return id;
            var authored = Profiles.Get(id);
            return authored != null && authored.Length <= Profiles.Chamfer.Length ? id : ProfileId.Chamfer;
        }

        /// <summary><see cref="Sectioned"/> resolved to points, for callers that sweep an array
        /// rather than an id. <see cref="ProfileId.None"/> comes back null, as
        /// <see cref="Profiles.Get"/> defines it.</summary>
        public static Vector2[] Section(ProfileId id, DetailLevel detail)
            => Profiles.Get(Sectioned(id, detail));

        /// <summary>Cells across one axis of a subdivided field — window lights, door panels, garage
        /// slats, storefront bays. <see cref="DetailLevel.Reduced"/> halves them, which halves the
        /// bars between them with them: <c>n</c> cells carry <c>n-1</c> bars. Never below one, so a
        /// field never disappears at a level that is only supposed to cheapen it.</summary>
        public static int Divisions(int n, DetailLevel detail)
            => detail == DetailLevel.Full ? Mathf.Max(n, 1) : Mathf.Max((n + 1) / 2, 1);

        /// <summary>The <see cref="DetailLevel.Flat"/> floor: one role-coloured quad standing in for
        /// the whole part, rising from y = 0 to <paramref name="h"/> and centred on the anchor. Two
        /// triangles — deliberately exactly the placeholder quad the generators replaced, so the
        /// cheapest level can never be a regression on what shipped before them.</summary>
        public static void FlatQuad(MeshBuilder mb, float w, float h, float z, MaterialRole role)
        {
            mb.BeginRole(role);
            Vector3 n = Vector3.forward;
            float hw = w * 0.5f;
            int a = mb.Vert(new Vector3(-hw, 0f, z), n);
            int b = mb.Vert(new Vector3(hw, 0f, z), n);
            int c = mb.Vert(new Vector3(hw, h, z), n);
            int d = mb.Vert(new Vector3(-hw, h, z), n);
            mb.QuadFacing(a, b, c, d, n);
        }
    }
}
