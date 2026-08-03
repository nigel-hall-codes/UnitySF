using System.Collections.Generic;
using UnityEngine;
using SFMap.Pipeline.Buildings;
using SFMap.Pipeline.Buildings.Gen;

namespace SFMap.Pipeline.Editor
{
    /// <summary>
    /// Where a building's street facades <b>meet</b> (#459). Built once per building from the
    /// #279 sidecar's per-facade edge geometry; answers the two questions the assembler's
    /// single-facade placement model cannot:
    ///
    /// <list type="number">
    /// <item><b>Collision.</b> <see cref="Blocked"/> — would this artifact, placed at this
    /// normalized x, occupy the corner volume the adjacent facade's artifacts already claim?
    /// A projecting part (bay, awning, balcony) near <c>nx ≈ 1</c> on one facade and one near
    /// <c>nx ≈ 0</c> on the next are the same corner of the same building, and nothing else in the
    /// pipeline reconciles them.</item>
    /// <item><b>Continuity.</b> <see cref="TryBuildRun"/> — the chained polyline a banding artifact
    /// (cornice, belt course, water table) should sweep along so it wraps the corner as one mitred
    /// run instead of stopping dead at each facade's end.</item>
    /// </list>
    ///
    /// <para><b>Scope, deliberately narrow.</b> Only <c>street_facades[]</c> participates. A
    /// building with one street facade has no corners, so every query here is a no-op and its
    /// assembly is bit-for-bit what it was before this type existed. That is the whole
    /// "non-corner buildings are unaffected" guarantee, and it is structural rather than
    /// conditional.</para>
    ///
    /// <para>Pure and allocation-light: no randomness, no asset access, no Unity Editor API — the
    /// corner rule can therefore never perturb the seeded placement draws
    /// (<c>hash(osm_id, ruleIndex, slotIndex)</c>) that make a re-import byte-stable.</para>
    /// </summary>
    public sealed class FacadeCornerTable
    {
        /// <summary>How close two facade endpoints must be to count as the same building corner.
        /// Adjacent edges of one footprint share a vertex exactly, so this only has to absorb the
        /// double→float conversion in the sidecar; 25 cm is generous for that and still far below
        /// any real facade length.</summary>
        public const float JoinEpsilonMeters = 0.25f;

        /// <summary>Two street facades meeting at one point of the footprint.</summary>
        public readonly struct Corner
        {
            /// <summary>Index into <c>street_facades[]</c>.</summary>
            public readonly int facadeA;
            public readonly int facadeB;

            /// <summary>True when the corner sits at that facade's <c>edge[2..3]</c> end
            /// (<c>nx = 1</c>); false at its <c>edge[0..1]</c> end (<c>nx = 0</c>).</summary>
            public readonly bool farEndA;
            public readonly bool farEndB;

            /// <summary>True for an outside corner — the building juts out, the usual street
            /// corner. False for a reflex/inside corner, an L-plan's notch, where the two facades
            /// face into each other and artifacts intrude much further into each other's space.</summary>
            public readonly bool convex;

            /// <summary>The shared footprint vertex, in world metres at y = 0.</summary>
            public readonly Vector3 point;

            public Corner(int facadeA, bool farEndA, int facadeB, bool farEndB, bool convex, Vector3 point)
            {
                this.facadeA = facadeA;
                this.farEndA = farEndA;
                this.facadeB = facadeB;
                this.farEndB = farEndB;
                this.convex = convex;
                this.point = point;
            }

            /// <summary>Which end of <paramref name="facade"/> this corner sits at, or false with
            /// <paramref name="found"/> false when the facade isn't part of this corner.</summary>
            public bool EndOf(int facade, out bool found)
            {
                if (facade == facadeA) { found = true; return farEndA; }
                if (facade == facadeB) { found = true; return farEndB; }
                found = false;
                return false;
            }
        }

        readonly StreetFacadeJson[] _facades;
        readonly List<Corner> _corners;

        FacadeCornerTable(StreetFacadeJson[] facades, List<Corner> corners)
        {
            _facades = facades;
            _corners = corners;
        }

        public IReadOnlyList<Corner> Corners => _corners;
        public bool HasCorners => _corners.Count > 0;
        public int FacadeCount => _facades != null ? _facades.Length : 0;

        /// <summary>Find every pair of street facades that share a footprint vertex. O(n²) over a
        /// handful of facades — a corner building has two or three, a plaza block maybe six.</summary>
        public static FacadeCornerTable Build(BuildingFactsJson facts)
        {
            var facades = facts != null ? facts.street_facades : null;
            var corners = new List<Corner>();
            if (facades == null || facades.Length < 2)
                return new FacadeCornerTable(facades, corners);

            for (int i = 0; i < facades.Length; i++)
            {
                if (!HasEdge(facades[i])) continue;
                for (int j = i + 1; j < facades.Length; j++)
                {
                    if (!HasEdge(facades[j])) continue;
                    if (TryJoin(facades, i, j, out var corner)) corners.Add(corner);
                }
            }
            return new FacadeCornerTable(facades, corners);
        }

        // The closest coincident endpoint pairing of two facades, if any. Only one corner per pair:
        // two edges of a simple footprint share at most one vertex, and taking the closest pairing
        // keeps the result independent of the order the four combinations are tested in.
        static bool TryJoin(StreetFacadeJson[] facades, int i, int j, out Corner corner)
        {
            corner = default;
            float best = JoinEpsilonMeters * JoinEpsilonMeters;
            bool found = false;
            bool bestFarI = false, bestFarJ = false;

            for (int ei = 0; ei < 2; ei++)
            {
                for (int ej = 0; ej < 2; ej++)
                {
                    float d = (Endpoint(facades[i], ei == 1) - Endpoint(facades[j], ej == 1)).sqrMagnitude;
                    if (d > best) continue;
                    best = d;
                    bestFarI = ei == 1;
                    bestFarJ = ej == 1;
                    found = true;
                }
            }
            if (!found) return false;

            // Convexity from the turn, not from the angle between the normals — the two are not the
            // same test. Walk facade i toward the shared vertex: if that direction is heading OUT of
            // facade j's wall, the building turns away from itself and the corner is convex.
            Vector3 towardCorner = AlongToward(facades[i], bestFarI);
            Vector3 outwardJ = FacadeFrame.OutwardNormal(facades[j].bearing_deg);
            bool convex = Vector3.Dot(towardCorner, outwardJ) > 0f;

            Vector3 p = (Endpoint(facades[i], bestFarI) + Endpoint(facades[j], bestFarJ)) * 0.5f;
            corner = new Corner(i, bestFarI, j, bestFarJ, convex, p);
            return true;
        }

        /// <summary>
        /// Would a part placed at <paramref name="nx"/> on <paramref name="facade"/> fight with what
        /// the adjacent facade puts in the same corner? The caller passes the part's real extents,
        /// taken from the generated mesh, so the rule needs no authored parameter and adapts to
        /// whatever a family actually builds.
        ///
        /// <para><b>Convex corner</b> (a normal street corner): work it out in plan at a right
        /// angle and the two artifacts' volumes only intersect if <i>both</i> overhang the shared
        /// vertex — each one's projection lives in the half-space its own wall faces, which the
        /// other's wall does not reach into. So keeping each part's own footprint on its own facade
        /// is sufficient, and the clearance is just its overhang.</para>
        ///
        /// <para><b>Reflex corner</b> (an L-plan notch): the walls face into each other, so the
        /// neighbour's projection reaches <paramref name="projectionMeters"/> along this facade past
        /// the vertex and the clearance has to absorb that as well. The neighbour's projection is
        /// estimated as this part's own — right whenever both facades are dressed by the same
        /// <c>Facade.Street</c> rule, which is the overwhelming case, and conservative-ish
        /// otherwise.</para>
        ///
        /// <para>Both figures assume a right-angle corner; SF's grid makes that nearly always true,
        /// and an acute prow would want more clearance than this gives. Stated rather than
        /// silently approximated.</para>
        /// </summary>
        /// <param name="facade">The facade being dressed; matched into this table by reference and
        /// then by <c>edge_index</c>, so a Front/Left/Right placement resolving onto a street edge
        /// is reconciled too.</param>
        /// <param name="nx">Normalized position along the facade, as <c>PlacePart</c> uses it.</param>
        /// <param name="partMinX">The part's along-facade extent below its anchor, in metres,
        /// already scaled by the placement scale (i.e. mesh <c>bounds.min.x</c>, normally negative).</param>
        /// <param name="partMaxX">Its extent above the anchor (mesh <c>bounds.max.x</c>).</param>
        /// <param name="projectionMeters">How far it stands proud of the wall — mount depth plus
        /// the mesh's outward extent.</param>
        public bool Blocked(StreetFacadeJson facade, float nx,
                            float partMinX, float partMaxX, float projectionMeters)
        {
            if (_corners.Count == 0) return false;
            int index = IndexOf(facade);
            if (index < 0) return false;

            float len = EdgeLength(_facades[index]);
            if (len < 1e-3f) return false;

            float x = Mathf.Clamp01(nx) * len;
            float intrusion = Mathf.Max(projectionMeters, 0f);

            for (int c = 0; c < _corners.Count; c++)
            {
                bool far = _corners[c].EndOf(index, out bool onThisFacade);
                if (!onThisFacade) continue;

                float clearance = _corners[c].convex ? 0f : intrusion;
                if (far)
                {
                    if (x + partMaxX > len - clearance) return true;
                }
                else
                {
                    if (x + partMinX < clearance) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Chain the street facades through their shared corners into one polyline for a banding
        /// artifact to sweep along, at height <paramref name="y"/> and standing
        /// <paramref name="outwardOffsetMeters"/> proud of the wall.
        ///
        /// <para>The offset is mitred, not applied per edge: at an interior vertex the point moves
        /// along <c>(n1 + n2) / (1 + n1·n2)</c>, the one displacement that lands exactly
        /// <paramref name="outwardOffsetMeters"/> off <i>both</i> wall planes. Offsetting each edge
        /// independently would leave a notch at every corner — the same defect
        /// <see cref="Kernels.ProfileSweep"/>'s mitre exists to avoid one level down.</para>
        ///
        /// <para>False when there is no usable facade geometry. True with a two-point run for a
        /// single-facade building: a run is well defined there, it simply has no corners to
        /// mitre.</para>
        /// </summary>
        public bool TryBuildRun(float y, float outwardOffsetMeters, out FacadeRun run)
        {
            run = default;
            if (_facades == null || _facades.Length == 0) return false;

            int start = FindRunStart(out bool startForward);
            if (start < 0) return false;

            // Walk the chain from `start`, recording which way each facade is traversed.
            var order = new List<int>();
            var forward = new List<bool>();
            var seen = new bool[_facades.Length];
            bool closed = false;

            int cur = start;
            bool fwd = startForward;
            while (cur >= 0 && !seen[cur])
            {
                seen[cur] = true;
                order.Add(cur);
                forward.Add(fwd);

                int ci = LinkAt(cur, fwd);          // corner at the end we exit through
                if (ci < 0) break;                  // free end: the run stops here

                var corner = _corners[ci];
                int next = corner.facadeA == cur ? corner.facadeB : corner.facadeA;
                bool nextFar = corner.facadeA == cur ? corner.farEndB : corner.farEndA;
                if (next == start) { closed = true; break; }
                if (seen[next]) break;              // a non-simple chain: stop rather than loop
                // Entering at the far end means traversing that facade backwards.
                fwd = !nextFar;
                cur = next;
            }

            // Vertices: each facade contributes the endpoint it is entered through, plus (when the
            // run is open) the last facade's exit endpoint. A closed run's final exit IS points[0].
            int n = order.Count + (closed ? 0 : 1);
            if (n < 2) return false;

            var points = new Vector3[n];
            var normals = new Vector3[n];
            for (int i = 0; i < order.Count; i++)
                points[i] = Endpoint(_facades[order[i]], !forward[i]);
            if (!closed)
                points[n - 1] = Endpoint(_facades[order[order.Count - 1]], forward[order.Count - 1]);

            for (int i = 0; i < n; i++)
            {
                // The facades meeting at point i: the one entered here (order[i], when it exists)
                // and the one exited here (order[i-1], wrapping when closed).
                int outgoing = i < order.Count ? order[i] : -1;
                int incoming = i > 0 ? order[i - 1] : (closed ? order[order.Count - 1] : -1);

                Vector3 nOut = outgoing >= 0 ? FacadeFrame.OutwardNormal(_facades[outgoing].bearing_deg) : Vector3.zero;
                Vector3 nIn = incoming >= 0 ? FacadeFrame.OutwardNormal(_facades[incoming].bearing_deg) : Vector3.zero;

                Vector3 unit, offset;
                if (outgoing >= 0 && incoming >= 0)
                {
                    float dot = Vector3.Dot(nIn, nOut);
                    Vector3 sum = nIn + nOut;
                    if (sum.sqrMagnitude < 1e-8f || 1f + dot < 1e-4f)
                    {
                        // 180° reversal — no mitre exists; fall back to one wall's normal rather
                        // than launching the point to infinity.
                        unit = nIn;
                        offset = nIn * outwardOffsetMeters;
                    }
                    else
                    {
                        unit = sum.normalized;
                        offset = sum * (outwardOffsetMeters / (1f + dot));
                    }
                }
                else
                {
                    unit = outgoing >= 0 ? nOut : nIn;
                    offset = unit * outwardOffsetMeters;
                }

                normals[i] = unit;
                points[i] = new Vector3(points[i].x + offset.x, y, points[i].z + offset.z);
            }

            run = new FacadeRun(points, normals, closed);
            return run.IsValid;
        }

        // Prefer a facade with a free start end so an open run is walked from one of its ends;
        // otherwise every facade is in a cycle and any starting point closes the same loop.
        int FindRunStart(out bool forward)
        {
            for (int i = 0; i < _facades.Length; i++)
            {
                if (!HasEdge(_facades[i])) continue;
                if (LinkAt(i, false) < 0) { forward = true; return i; }    // nothing at its near end
                if (LinkAt(i, true) < 0) { forward = false; return i; }    // nothing at its far end
            }
            for (int i = 0; i < _facades.Length; i++)
                if (HasEdge(_facades[i])) { forward = true; return i; }
            forward = true;
            return -1;
        }

        // The corner attached to facade `index` at the end reached by traversing it in direction
        // `forward` (forward exits at the far end), or -1.
        int LinkAt(int index, bool forward)
        {
            for (int c = 0; c < _corners.Count; c++)
            {
                bool far = _corners[c].EndOf(index, out bool onIt);
                if (onIt && far == forward) return c;
            }
            return -1;
        }

        int IndexOf(StreetFacadeJson facade)
        {
            if (facade == null || _facades == null) return -1;
            for (int i = 0; i < _facades.Length; i++)
                if (ReferenceEquals(_facades[i], facade)) return i;
            for (int i = 0; i < _facades.Length; i++)
                if (_facades[i] != null && _facades[i].edge_index == facade.edge_index) return i;
            return -1;
        }

        static bool HasEdge(StreetFacadeJson f) => f != null && f.edge != null && f.edge.Length >= 4;

        /// <summary>One end of a facade edge, at y = 0. <paramref name="far"/> selects
        /// <c>edge[2..3]</c> (the <c>nx = 1</c> end).</summary>
        public static Vector3 Endpoint(StreetFacadeJson f, bool far)
            => far ? new Vector3(f.edge[2], 0f, f.edge[3]) : new Vector3(f.edge[0], 0f, f.edge[1]);

        /// <summary>Unit direction along the facade pointing at the given end.</summary>
        static Vector3 AlongToward(StreetFacadeJson f, bool far)
            => (Endpoint(f, far) - Endpoint(f, !far)).normalized;

        /// <summary>Facade width in metres, 0 when the sidecar carried no edge.</summary>
        public static float EdgeLength(StreetFacadeJson f)
        {
            if (!HasEdge(f)) return 0f;
            float dx = f.edge[2] - f.edge[0], dz = f.edge[3] - f.edge[1];
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
