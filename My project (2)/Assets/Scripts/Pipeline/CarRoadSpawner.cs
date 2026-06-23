using System;
using UnityEngine;

namespace SFMap.Pipeline
{
    /// <summary>
    /// Drops this car onto a road that streams in at runtime, so there's no need to
    /// import the whole static map into the scene.
    ///
    /// On <see cref="Start"/> it parks the car at <see cref="spawnAnchor"/> with its
    /// Rigidbody frozen, then waits for <see cref="ChunkStreamer"/> to stream in the
    /// chunk around the anchor. Chunk prefabs bake their roads with MeshColliders on
    /// the "Road" layer, so once a chunk is resident a downward raycast finds the
    /// nearest road; the car is moved there (plus a small clearance) and physics is
    /// restored so it settles onto the surface.
    ///
    /// Crucially it only accepts a road that belongs to a <b>streamed</b> chunk (a
    /// child of the ChunkStreamer). The streamer destroys the static "SF Map" on Play,
    /// so latching onto the static map would drop the car onto ground that's about to
    /// vanish — that's what makes it fall through. Waiting for streamed ground (and
    /// keeping the car still meanwhile, so the streamer's target doesn't move and the
    /// surrounding chunks load fully) avoids both problems.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CarRoadSpawner : MonoBehaviour
    {
        [Tooltip("World XZ to search for a road around. The car waits here (frozen) while chunks stream in. " +
                 "Point the ChunkStreamer's target at this car so the chunk here actually loads.")]
        public Vector3 spawnAnchor = Vector3.zero;

        [Tooltip("How far out (metres) from the anchor to search for a road.")]
        public float searchRadius = 400f;

        [Tooltip("Height above the road the car is dropped from so it settles cleanly.")]
        public float dropClearance = 1.5f;

        [Tooltip("Drop the car at the anchor anyway if no road streams in within this many seconds.")]
        public float timeoutSeconds = 30f;

        [Tooltip("Seconds between road searches while waiting for chunks to stream in.")]
        public float pollInterval = 0.25f;

        async void Start()
        {
            int roadLayer = LayerMask.NameToLayer("Road");
            if (roadLayer < 0)
            {
                Debug.LogWarning("[CarRoadSpawner] No 'Road' layer in the project — bake/import a map so it exists. " +
                                 "Leaving the car where it is.", this);
                return;
            }
            int mask = 1 << roadLayer;

            // Only accept roads under the streamer, so we never latch onto the static
            // "SF Map" that ChunkStreamer destroys on Play.
            var streamer = FindFirstObjectByType<ChunkStreamer>();
            Transform streamRoot = streamer != null ? streamer.transform : null;
            if (streamRoot == null)
                Debug.LogWarning("[CarRoadSpawner] No ChunkStreamer in the scene — cannot tell streamed roads from the " +
                                 "static map. Add one (Prometeo > Add Road Streaming To Car).", this);

            var rb = GetComponent<Rigidbody>();
            bool wasKinematic = rb.isKinematic;

            // Park the car above the anchor and freeze it so it neither falls through
            // the world nor drifts (which would move the streamer's target) before the
            // ground chunk has streamed in.
            rb.isKinematic = true;
            transform.position = spawnAnchor + Vector3.up * dropClearance;

            var token = destroyCancellationToken;
            var placement = spawnAnchor + Vector3.up * dropClearance;
            bool found = false;
            float waited = 0f;

            try
            {
                while (waited < timeoutSeconds)
                {
                    if (TryFindStreamedRoad(spawnAnchor, searchRadius, mask, dropClearance, streamRoot, out placement))
                    {
                        found = true;
                        break;
                    }
                    await Awaitable.WaitForSecondsAsync(pollInterval, token);
                    waited += pollInterval;
                }
            }
            catch (OperationCanceledException)
            {
                return; // destroyed while waiting
            }

            if (!found)
                Debug.LogWarning($"[CarRoadSpawner] No streamed road appeared within {timeoutSeconds}s near {spawnAnchor}. " +
                                 "Dropping the car at the anchor — check the ChunkStreamer's preset and that its " +
                                 "target points at this car.", this);

            transform.position = placement;
            transform.rotation = Quaternion.identity;

            rb.isKinematic = wasKinematic;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        /// Scans outward from <paramref name="near"/> on the XZ plane and raycasts
        /// straight down onto the road layer, returning the nearest road surface that
        /// belongs to a streamed chunk (plus clearance). Returns false until such a
        /// chunk is resident, so the car stays frozen until real ground exists.
        static bool TryFindStreamedRoad(Vector3 near, float maxRadius, int mask, float clearance,
                                        Transform streamRoot, out Vector3 placement)
        {
            placement = default;

            const float rayHeight = 1000f; // start well above any terrain
            const float rayLength = 2000f;
            const float step      = 4f;    // metres between samples (also the ring spacing)

            for (float r = 0f; r <= maxRadius; r += step)
            {
                int samples = Mathf.Max(1, Mathf.RoundToInt(2f * Mathf.PI * r / step));
                for (int i = 0; i < samples; i++)
                {
                    float a = i / (float)samples * Mathf.PI * 2f;
                    var origin = new Vector3(near.x + Mathf.Cos(a) * r, rayHeight, near.z + Mathf.Sin(a) * r);
                    if (!Physics.Raycast(origin, Vector3.down, out var hit, rayLength, mask, QueryTriggerInteraction.Ignore))
                        continue;

                    // Reject the static map's roads — only stand on a streamed chunk.
                    if (streamRoot != null && !hit.collider.transform.IsChildOf(streamRoot))
                        continue;

                    placement = hit.point + Vector3.up * clearance;
                    return true;
                }
            }
            return false;
        }
    }
}
