using UnityEngine;

namespace SFMap.Pipeline
{
    public static class ColliderUtil
    {
        /// <summary>
        /// Ensure <paramref name="go"/> has a solid (non-trigger) collider: if it already has
        /// colliders, enable them and clear their trigger flag; otherwise fit a single BoxCollider
        /// to the combined renderer bounds. Shared by ParkedCarStreamer and TrafficManager (#429) —
        /// both need streamed vehicles to be solid so the player/cars collide with them.
        /// </summary>
        public static void EnsureSolid(GameObject go)
        {
            var cols = go.GetComponentsInChildren<Collider>();
            bool hasCollider = cols.Length > 0;
            foreach (var col in cols) { col.enabled = true; col.isTrigger = false; }
            if (hasCollider) return;

            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return; // nothing visible to fit a box to

            Bounds world = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) world.Encapsulate(renderers[i].bounds);

            var box = go.AddComponent<BoxCollider>();
            var w2l = go.transform.worldToLocalMatrix;
            box.center = w2l.MultiplyPoint3x4(world.center);
            Vector3 size = w2l.MultiplyVector(world.size);
            box.size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
        }
    }
}
