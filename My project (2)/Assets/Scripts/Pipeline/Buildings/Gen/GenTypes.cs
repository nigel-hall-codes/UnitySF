using System;
using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    // The small shared vocabulary of the procedural geometry kernel (#453, design #452 §D3/§D6).
    // Kept in one file for the same reason BuildingEnums.cs exists: every kernel, generator and
    // cache references one definition.

    /// <summary>Which faces of a <see cref="Kernels.Box"/> to emit. Anything mounted flush to a
    /// wall never shows its <see cref="Back"/> face, and roughly a third of a facade's box
    /// triangles are exactly that (design #452 generators.md §2).</summary>
    [Flags]
    public enum Faces
    {
        None = 0,
        Right = 1 << 0,   // +X
        Left = 1 << 1,   // -X
        Top = 1 << 2,   // +Y
        Bottom = 1 << 3,   // -Y
        Front = 1 << 4,   // +Z — outward, toward the street
        Back = 1 << 5,   // -Z — into the wall

        All = Right | Left | Top | Bottom | Front | Back,

        /// <summary>Everything but the wall-facing face — the default for flush-mounted parts.</summary>
        NoBack = All & ~Back,
    }

    /// <summary>Triangle budget a generator must honour (design #452 D6 — the budget is a
    /// first-class parameter, not an afterthought). <see cref="Flat"/> is today's placeholder
    /// quad, which makes it a safe floor rather than a new failure mode.</summary>
    public enum DetailLevel { Full, Reduced, Flat }

    /// <summary>A named cross-section from the <see cref="Profiles"/> table. This enum plus that
    /// table is where the architectural vocabulary lives — a dozen arrays of numbers, not a
    /// modelling task (design #452 generators.md §3).</summary>
    public enum ProfileId { None, Flat, Chamfer, Cove, Ogee, Bullnose, Sill, Dentil, Corbel }

    /// <summary>
    /// What <see cref="MeshBuilder.Finish"/> hands back: a mesh plus the material role of each of
    /// its submeshes, index-aligned. This is deliberately the *same* pair
    /// <c>BuildingAssembler.CloneRoleColored</c> already reads off
    /// <see cref="BuildingPart.submeshRoles"/>, so the palette → vertex-colour path in
    /// <c>BakeAndCombine</c> needs no change at all (#453 acceptance 3, design #452 D4).
    /// </summary>
    public readonly struct PartMesh
    {
        public readonly Mesh mesh;
        public readonly MaterialRole[] submeshRoles;

        public PartMesh(Mesh mesh, MaterialRole[] submeshRoles)
        {
            this.mesh = mesh;
            this.submeshRoles = submeshRoles;
        }

        public bool IsValid => mesh != null && submeshRoles != null;
    }
}
