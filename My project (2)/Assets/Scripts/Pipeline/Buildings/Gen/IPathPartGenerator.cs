using UnityEngine;

namespace SFMap.Pipeline.Buildings.Gen
{
    /// <summary>
    /// A run of wall the placement layer has already chained together: the polyline a banding
    /// artifact follows across <b>one or more</b> facades of a building, plus the outward direction
    /// at every vertex (#459).
    ///
    /// <para>This exists because a cornice on a corner building is not two cornices. Sweeping a
    /// profile per facade stops the moulding dead at each facade's end and leaves the corner open;
    /// the building reads as two flats pushed together. One sweep along a chained path lets
    /// <see cref="Kernels.ProfileSweep"/> mitre the turn exactly as it already mitres the corners of
    /// a closed <see cref="Paths.Rect"/> — the geometry half of the problem was solved in #453.</para>
    ///
    /// <para><b>Frame.</b> Unlike a single-facade part, a run has no single bearing, so its
    /// part-local frame is <b>world-axis-aligned with the origin at <c>points[0]</c></b> — the
    /// assembler places a wrapped part with identity rotation. That is why
    /// <see cref="outward"/> is carried explicitly: "which way is out of the wall" changes along
    /// the run and cannot be recovered from a single up-hint. At an interior vertex it is the unit
    /// bisector of the two adjacent facades' outward normals, so a generator can build a mitre
    /// plane from it directly.</para>
    ///
    /// <para><see cref="points"/> already include the part's mount depth: the placement layer
    /// offsets the wall line outward by the mitre-correct amount, so a generator sweeps its profile
    /// about the path it is given and does not re-apply the offset.</para>
    /// </summary>
    public readonly struct FacadeRun
    {
        /// <summary>Ordered polyline in part-local space (see the frame remarks). For a
        /// <see cref="closed"/> run the last point does <b>not</b> repeat the first.</summary>
        public readonly Vector3[] points;

        /// <summary>Unit horizontal outward direction at each point, index-aligned to
        /// <see cref="points"/>.</summary>
        public readonly Vector3[] outward;

        /// <summary>True when the run closes back on itself — every street facade of the building
        /// chained into a loop. Sweep it with <c>closedPath: true</c>.</summary>
        public readonly bool closed;

        public FacadeRun(Vector3[] points, Vector3[] outward, bool closed)
        {
            this.points = points;
            this.outward = outward;
            this.closed = closed;
        }

        public bool IsValid => points != null && outward != null &&
                               points.Length >= 2 && outward.Length == points.Length;

        /// <summary>Straight segments in the run — one fewer than the points when open, one per
        /// point when closed.</summary>
        public int SegmentCount => points == null ? 0 : (closed ? points.Length : points.Length - 1);

        /// <summary>Turns the run makes, i.e. corners a sweep along it has to mitre. Zero for a
        /// single-facade run, which is exactly why a non-corner building is unaffected.</summary>
        public int CornerCount => points == null ? 0 : (closed ? points.Length : points.Length - 2);

        /// <summary>Total run length in metres.</summary>
        public float Length
        {
            get
            {
                if (points == null) return 0f;
                float sum = 0f;
                for (int i = 0; i < SegmentCount; i++)
                    sum += (points[(i + 1) % points.Length] - points[i]).magnitude;
                return sum;
            }
        }

        /// <summary>The same run with <paramref name="anchor"/> subtracted from every point — how
        /// the assembler turns the world-space run it computes into the part-local one a generator
        /// receives. Outward directions are unaffected (they are directions, not positions).</summary>
        public FacadeRun Rebased(Vector3 anchor)
        {
            if (points == null) return this;
            var moved = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++) moved[i] = points[i] - anchor;
            return new FacadeRun(moved, outward, closed);
        }
    }

    /// <summary>
    /// A generator whose geometry follows a path handed to it by the placement layer instead of one
    /// derived entirely from its own parameters (#459). <b>Banding</b> artifacts are these —
    /// cornices, belt courses, water tables, trim bands — because on a corner building the band is
    /// one continuous mitred run around the corner, not one part per facade.
    ///
    /// <para>It extends <see cref="IPartGenerator"/> rather than replacing it, so a path-aware
    /// family is still discovered and registered by <see cref="PartGenerators"/> with no change,
    /// and <see cref="IPartGenerator.Generate"/> stays the single-facade fallback the assembler
    /// uses when no run can be built.</para>
    /// </summary>
    public interface IPathPartGenerator : IPartGenerator
    {
        /// <summary>Write the part into <paramref name="mb"/> along <paramref name="run"/> and
        /// return its <see cref="MeshBuilder.Finish"/> result, in the run's part-local frame (world
        /// axes, origin at <c>run.points[0]</c>). Returning an invalid <see cref="PartMesh"/> makes
        /// the assembler skip the placement, exactly as an invalid
        /// <see cref="IPartGenerator.Generate"/> result does.</summary>
        PartMesh GenerateAlong(PartParams p, FacadeRun run, MeshBuilder mb);
    }
}
