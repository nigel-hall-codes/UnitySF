using UnityEngine;

namespace SFMap.Pipeline
{
    /// <summary>
    /// Draws road-network gizmos in the Scene view while the game is running:
    /// centerlines coloured by lane count, and direction arrows showing each
    /// directed edge. All roads are currently 2-way (forward + reverse pairs),
    /// so every road shows arrows in both directions.
    ///
    /// Add to any GameObject in the scene. Gizmos appear as soon as
    /// <see cref="RoadNetwork"/> finishes loading its graph.
    /// </summary>
    [AddComponentMenu("SFMap/Road Network Debug View")]
    public class RoadNetworkDebugView : MonoBehaviour
    {
        [Header("Visibility")]
        public bool showLanes = true;
        public bool showDirection = true;

        [Header("Colors")]
        public Color singleLaneColor = new Color(0f, 0.8f, 1f, 0.8f);
        public Color multiLaneColor  = new Color(1f, 0.75f, 0f, 0.8f);

        [Header("Lane Threshold")]
        [Tooltip("Roads at least this wide are treated as multi-lane. Match TrafficManager.multiLaneMinWidth.")]
        [Min(0f)] public float multiLaneMinWidth = 6.5f;

        [Header("Gizmos")]
        [Tooltip("Y offset above the terrain surface so lines sit on top of roads.")]
        public float heightOffset = 0.5f;

        [Tooltip("Arrow head size in metres.")]
        [Min(0.5f)] public float arrowSize = 5f;

        void OnDrawGizmos()
        {
            var net = RoadNetwork.Instance;
            if (net == null || !net.IsReady) return;

            for (int i = 0; i < net.EdgeCount; i++)
            {
                var edge = net.GetEdge(i);

                // Each road is a (forward, reverse) index pair. Skip the higher index so
                // we draw each physical road exactly once.
                if (i > edge.Reverse) continue;

                var pts = edge.Points;
                if (pts == null || pts.Length < 2) continue;

                // Skip roads with no points on a currently-loaded terrain tile —
                // unloaded chunks have no heightmap to sample so lines would appear underground.
                if (!AnyPointOnActiveTerrain(pts)) continue;

                bool isMultiLane = edge.Width >= multiLaneMinWidth;
                Gizmos.color = isMultiLane ? multiLaneColor : singleLaneColor;

                if (showLanes)
                {
                    for (int j = 0; j + 1 < pts.Length; j++)
                        Gizmos.DrawLine(ToWorld(pts[j]), ToWorld(pts[j + 1]));
                }

                if (showDirection)
                {
                    // Forward arrow at 35% along the edge; reverse arrow at 65%.
                    DrawArrowAlongEdge(pts, fraction: 0.35f, forward: true);
                    DrawArrowAlongEdge(pts, fraction: 0.65f, forward: false);
                }
            }
        }

        Vector3 ToWorld(Vector2 p)
        {
            var world = new Vector3(p.x, 0f, p.y);
            world.y = SampleTerrainHeight(world) + heightOffset;
            return world;
        }

        static float SampleTerrainHeight(Vector3 worldPos)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                var tp = terrain.GetPosition();
                var sz = terrain.terrainData.size;
                if (worldPos.x >= tp.x && worldPos.x <= tp.x + sz.x &&
                    worldPos.z >= tp.z && worldPos.z <= tp.z + sz.z)
                    return tp.y + terrain.SampleHeight(worldPos);
            }
            return 0f;
        }

        static bool AnyPointOnActiveTerrain(Vector2[] pts)
        {
            foreach (var p in pts)
            {
                var world = new Vector3(p.x, 0f, p.y);
                foreach (var terrain in Terrain.activeTerrains)
                {
                    var tp = terrain.GetPosition();
                    var sz = terrain.terrainData.size;
                    if (world.x >= tp.x && world.x <= tp.x + sz.x &&
                        world.z >= tp.z && world.z <= tp.z + sz.z)
                        return true;
                }
            }
            return false;
        }

        void DrawArrowAlongEdge(Vector2[] pts, float fraction, bool forward)
        {
            float total = 0f;
            for (int j = 0; j + 1 < pts.Length; j++)
                total += Vector2.Distance(pts[j], pts[j + 1]);

            float target = total * fraction;
            float acc = 0f;

            for (int j = 0; j + 1 < pts.Length; j++)
            {
                float seg = Vector2.Distance(pts[j], pts[j + 1]);
                if (acc + seg >= target || j == pts.Length - 2)
                {
                    float t = seg > 0f ? Mathf.Clamp01((target - acc) / seg) : 0.5f;
                    var a = ToWorld(pts[j]);
                    var b = ToWorld(pts[j + 1]);
                    var mid = Vector3.Lerp(a, b, t);
                    var dir = (forward ? b - a : a - b).normalized;
                    DrawArrowHead(mid, dir);
                    return;
                }
                acc += seg;
            }
        }

        // Draws a V-shape arrowhead: two lines from `tip` angled backward-left and backward-right.
        void DrawArrowHead(Vector3 tip, Vector3 dir)
        {
            var right = Vector3.Cross(Vector3.up, dir).normalized;
            var tail  = tip - dir * arrowSize;
            Gizmos.DrawLine(tip, tail + right * (arrowSize * 0.4f));
            Gizmos.DrawLine(tip, tail - right * (arrowSize * 0.4f));
        }
    }
}
