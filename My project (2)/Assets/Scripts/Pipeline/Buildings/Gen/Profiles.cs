using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// The named cross-sections <see cref="Kernels.ProfileSweep"/> extrudes (#453, design #452
    /// generators.md §3). A cornice, a sill, a casing and a handrail differ only in *which profile,
    /// along which path* — so this table, a dozen arrays of numbers, is where the architectural
    /// vocabulary actually lives.
    /// <para>
    /// <b>Profile space.</b> <c>x</c> is <i>across</i> — outward from the wall, normalised 0 (wall
    /// plane) … 1 (full projection). <c>y</c> is the lateral axis in the sweep frame, normalised
    /// −0.5 … +0.5 of the profile's size; for a horizontal run that is height, for a run around an
    /// opening it is the trim's width. Scale to metres with <see cref="Scaled"/>. This is the
    /// convention stated by <c>ProfileSweep</c>'s own signature in generators.md §3
    /// ("cross-section in (across, up); +across = outward from wall"); §5's <c>* (w, d)</c>
    /// pseudocode passes them the other way round and is loose.
    /// </para>
    /// <para>
    /// <b>Winding.</b> Points run so that the outward surface normal is the profile tangent rotated
    /// −90° in (x, y), i.e. <c>n = (t.y, −t.x)</c>. Every table below is authored to that rule.
    /// </para>
    /// </summary>
    public static class Profiles
    {
        /// <summary>The cheapest profile: one flat face standing proud of the wall, no returns.
        /// Plain bands, and the stand-in <see cref="DetailLevel.Reduced"/> degrades to.</summary>
        public static readonly Vector2[] Flat =
        {
            new Vector2(1f, -0.5f),
            new Vector2(1f,  0.5f),
        };

        /// <summary>A triangular chamfer, wall → apex → wall.</summary>
        public static readonly Vector2[] Chamfer =
        {
            new Vector2(0f, -0.5f),
            new Vector2(1f,  0f),
            new Vector2(0f,  0.5f),
        };

        /// <summary>Concave quarter-round — the simple cornice.</summary>
        public static readonly Vector2[] Cove =
        {
            new Vector2(0f,      -0.5f),
            new Vector2(0.3827f, -0.4239f),
            new Vector2(0.7071f, -0.2071f),
            new Vector2(0.9239f,  0.1173f),
            new Vector2(1f,       0.5f),
        };

        /// <summary>S-curve: a cove running into a round. Victorian cornice and casing.</summary>
        public static readonly Vector2[] Ogee =
        {
            new Vector2(0f,      -0.5f),
            new Vector2(0.25f,   -0.433f),
            new Vector2(0.433f,  -0.25f),
            new Vector2(0.5f,     0f),
            new Vector2(0.567f,   0.25f),
            new Vector2(0.75f,    0.433f),
            new Vector2(1f,       0.5f),
        };

        /// <summary>Half-round nose. Sills, treads, water tables.</summary>
        public static readonly Vector2[] Bullnose =
        {
            new Vector2(0f,      -0.5f),
            new Vector2(0.7071f, -0.3536f),
            new Vector2(1f,       0f),
            new Vector2(0.7071f,  0.3536f),
            new Vector2(0f,       0.5f),
        };

        /// <summary>Window sill: weathered top sloping out, nose, and a drip groove on the
        /// underside so rain lets go before it reaches the wall. Authored underside-first so the
        /// winding rule puts the normals where they belong.</summary>
        public static readonly Vector2[] Sill =
        {
            new Vector2(0f,    -0.05f),
            new Vector2(0.80f, -0.05f),
            new Vector2(0.85f, -0.18f),   // drip groove
            new Vector2(1f,    -0.10f),
            new Vector2(1f,     0.30f),   // nose face
            new Vector2(0f,     0.5f),    // weathered top, sloping out
        };

        /// <summary>One dentil block, open at the wall. The blocks' repeat along the path is the
        /// cornice family's business, not the kernel's — see <see cref="DentilPitch"/>.</summary>
        public static readonly Vector2[] Dentil =
        {
            new Vector2(0f, -0.5f),
            new Vector2(1f, -0.5f),
            new Vector2(1f,  0.5f),
            new Vector2(0f,  0.5f),
        };

        /// <summary>Classical dentil spacing as a multiple of block width — the repeat pitch the
        /// table in generators.md §3 lists alongside the four <see cref="Dentil"/> points.</summary>
        public const float DentilPitch = 2f;

        /// <summary>Stepped bracket — the SoMa brick cornice.</summary>
        public static readonly Vector2[] Corbel =
        {
            new Vector2(0f,    -0.5f),
            new Vector2(0.55f, -0.5f),
            new Vector2(0.55f,  0f),
            new Vector2(1f,     0f),
            new Vector2(1f,     0.5f),
        };

        /// <summary>Look up a profile by id. <see cref="ProfileId.None"/> returns null — callers
        /// treat that as "skip this element", exactly as the window family's
        /// <c>casingProfile == None</c> does.</summary>
        public static Vector2[] Get(ProfileId id)
        {
            switch (id)
            {
                case ProfileId.Flat: return Flat;
                case ProfileId.Chamfer: return Chamfer;
                case ProfileId.Cove: return Cove;
                case ProfileId.Ogee: return Ogee;
                case ProfileId.Bullnose: return Bullnose;
                case ProfileId.Sill: return Sill;
                case ProfileId.Dentil: return Dentil;
                case ProfileId.Corbel: return Corbel;
                default: return null;
            }
        }

        /// <summary>Normalised profile → metres: <paramref name="projection"/> is how far the
        /// moulding stands proud of the wall, <paramref name="size"/> its extent across the sweep
        /// frame. Returns a new array; the tables above are never mutated.</summary>
        public static Vector2[] Scaled(Vector2[] profile, float projection, float size)
        {
            if (profile == null) return null;
            var s = new Vector2[profile.Length];
            for (int i = 0; i < profile.Length; i++)
                s[i] = new Vector2(profile[i].x * projection, profile[i].y * size);
            return s;
        }

        /// <summary>Convenience overload for the common <c>Profiles.Scaled(id, …)</c> call.</summary>
        public static Vector2[] Scaled(ProfileId id, float projection, float size)
            => Scaled(Get(id), projection, size);
    }
}
