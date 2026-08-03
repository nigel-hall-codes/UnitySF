using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline.Editor
{
    /// <summary>
    /// What each facade edge is already <b>wearing</b> (#491). One table per building, written by
    /// every template-driven placement and consulted by every procedural one, replacing the
    /// exact-only point marks the assembler used to keep in <c>_exactMarks</c>.
    ///
    /// <para><b>Why a point mark was not enough.</b> The old mark was the placement's normalized
    /// <c>x</c> keyed by <c>(edge_index, floor)</c>, tested against a radius of
    /// <c>max(minSpacingMeters, spacing/2)</c>. Three things followed from that shape and all three
    /// were defects rather than choices:</para>
    /// <list type="number">
    /// <item><description>only <c>PlaceExact</c> wrote marks, so two procedural rules were blind to
    /// each other and a floor could hold at most one rule;</description></item>
    /// <item><description>the key named a single floor, so an artifact spanning two floors marked
    /// only its base and a rule placed straight into the floor it rises through;</description></item>
    /// <item><description>the radius could not exceed the rule's own repeat pitch without also
    /// changing <c>spacing</c> and therefore the slot count, so a 2.20 m pitch could never clear a
    /// 3.80 m bay.</description></item>
    /// </list>
    ///
    /// <para><b>The shape that fixes all three.</b> A span carries the real <i>extent</i> of what
    /// was placed, taken from the generated mesh's bounds exactly as the corner rule takes its
    /// figures (#459/#488): an along-facade interval in metres and a world-Y interval. Two
    /// placements conflict when both intervals overlap. Metres rather than normalized x removes the
    /// pitch ceiling; a Y interval rather than a floor index removes the floorsSpanned hole, with no
    /// parameter to author and nothing for a family to keep in sync with its own geometry — a
    /// two-floor bay is six metres tall because its mesh is six metres tall.</para>
    ///
    /// <para><b>Determinism.</b> Pure, allocation-light, no randomness. Placement order is fixed
    /// (roof, exact in template order, rules in rule order, then per facade / floor / slot), and the
    /// per-slot seeds are drawn before a placement is offered to this table, so refusing one slot
    /// cannot shift any other slot's draws.</para>
    /// </summary>
    public sealed class FacadeOccupancy
    {
        /// <summary>Overlaps shorter than this are contact, not interpenetration — the tolerance
        /// that keeps two parts authored to touch exactly (a mullion shared between neighbouring
        /// bays) from excluding each other.</summary>
        public const float TouchToleranceMeters = 0.005f;

        /// <summary>One placed part's claim on one facade edge.</summary>
        public readonly struct Span
        {
            public readonly int edgeIndex;
            /// <summary>Which directive placed it: the rule index for a procedural placement,
            /// <see cref="NotARule"/> for everything else. A rule never excludes against its own
            /// slots — keeping parts of one family apart is the repeat pitch's job, and letting the
            /// table do it as well would silently thin a rhythm the author tuned.</summary>
            public readonly int sourceId;
            public readonly float minMeters, maxMeters;
            public readonly float minY, maxY;

            public Span(int edgeIndex, int sourceId, float minMeters, float maxMeters,
                        float minY, float maxY)
            {
                this.edgeIndex = edgeIndex;
                this.sourceId = sourceId;
                this.minMeters = minMeters;
                this.maxMeters = maxMeters;
                this.minY = minY;
                this.maxY = maxY;
            }
        }

        /// <summary>Source id for a placement that belongs to no procedural rule.</summary>
        public const int NotARule = -1;

        readonly List<Span> _spans = new List<Span>();

        public int Count => _spans.Count;
        public IReadOnlyList<Span> Spans => _spans;

        public void Clear() => _spans.Clear();

        public void Add(int edgeIndex, int sourceId, float minMeters, float maxMeters,
                        float minY, float maxY)
            => _spans.Add(new Span(edgeIndex, sourceId, minMeters, maxMeters, minY, maxY));

        /// <summary>Would a part occupying this along-facade interval and this height band collide
        /// with something already placed on the same edge by a different directive?</summary>
        public bool Occupied(int edgeIndex, int sourceId, float minMeters, float maxMeters,
                             float minY, float maxY)
        {
            for (int i = 0; i < _spans.Count; i++)
            {
                var s = _spans[i];
                if (s.edgeIndex != edgeIndex) continue;
                if (sourceId != NotARule && s.sourceId == sourceId) continue;
                if (minMeters >= s.maxMeters - TouchToleranceMeters ||
                    maxMeters <= s.minMeters + TouchToleranceMeters) continue;
                if (minY >= s.maxY - TouchToleranceMeters ||
                    maxY <= s.minY + TouchToleranceMeters) continue;
                return true;
            }
            return false;
        }
    }
}
