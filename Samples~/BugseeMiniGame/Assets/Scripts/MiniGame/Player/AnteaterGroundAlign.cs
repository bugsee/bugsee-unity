using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Snaps the anteater visual to terrain after walk animation.
    /// Adjusts this transform (parent of the animated mesh) so Animator root curves
    /// cannot fight the correction. Uses paw bone tips when available.
    /// </summary>
    [DefaultExecutionOrder(2000)]
    public sealed class AnteaterGroundAlign : MonoBehaviour
    {
        [SerializeField] float sink = 0.02f;
        [SerializeField] float rayStart = 3f;
        [SerializeField] float rayLength = 8f;

        Transform _meshRoot;
        Transform[] _pawBones;

        public static AnteaterGroundAlign Attach(Transform anteaterRoot, Transform meshRoot)
        {
            var align = anteaterRoot.GetComponent<AnteaterGroundAlign>();
            if (align == null)
                align = anteaterRoot.gameObject.AddComponent<AnteaterGroundAlign>();
            align._meshRoot = meshRoot;
            align._pawBones = FindPawBones(meshRoot);
            return align;
        }

        static Transform[] FindPawBones(Transform meshRoot)
        {
            var list = new System.Collections.Generic.List<Transform>();
            var all = meshRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                string n = t.name.ToLowerInvariant();
                // Meshy quadruped tips are often *leg2 / *leg_2 / foot / paw / toe.
                bool tip =
                    n.EndsWith("leg2") || n.EndsWith("leg_2") || n.EndsWith("leg.2") ||
                    n.Contains("foot") || n.Contains("paw") || n.Contains("toe") ||
                    n.Contains("hoof");
                if (tip)
                    list.Add(t);
            }

            return list.ToArray();
        }

        void LateUpdate()
        {
            if (_meshRoot == null)
                return;

            if (!TryGroundHit(transform.position + Vector3.up * rayStart, out var hit))
                return;

            float feetY = MeasureFeetWorldY();
            float error = feetY - (hit.point.y + sink);
            if (Mathf.Abs(error) < 0.001f)
                return;

            // Move the NON-animated parent so Animator root curves on the mesh stay intact.
            var lp = transform.localPosition;
            lp.y -= error;
            transform.localPosition = lp;
        }

        float MeasureFeetWorldY()
        {
            float minY = float.PositiveInfinity;
            if (_pawBones != null && _pawBones.Length > 0)
            {
                for (int i = 0; i < _pawBones.Length; i++)
                {
                    if (_pawBones[i] != null)
                        minY = Mathf.Min(minY, _pawBones[i].position.y);
                }
            }

            if (!float.IsPositiveInfinity(minY))
                return minY;

            // Fallback: renderer bounds.
            var renderers = _meshRoot.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0)
                return transform.position.y;

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].enabled)
                    b.Encapsulate(renderers[i].bounds);
            }

            return b.min.y;
        }

        bool TryGroundHit(Vector3 origin, out RaycastHit best)
        {
            best = default;
            var hits = Physics.RaycastAll(origin, Vector3.down, rayLength, ~0, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit.collider == null)
                    continue;
                if (hit.collider is CharacterController)
                    continue;
                if (hit.collider.GetComponentInParent<AnteaterController>() != null)
                    continue;
                if (hit.collider.GetComponentInParent<AnteaterGeneratedVisual>() != null)
                    continue;
                // Skip vertical props (tree trunks, totems, beacons) — only walkable ground/hills.
                if (Vector3.Dot(hit.normal, Vector3.up) < 0.45f)
                    continue;
                if (hit.distance < bestDist)
                {
                    bestDist = hit.distance;
                    best = hit;
                    found = true;
                }
            }

            return found;
        }
    }
}
