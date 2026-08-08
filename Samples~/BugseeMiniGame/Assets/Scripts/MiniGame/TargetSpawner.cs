using System.Collections.Generic;
using UnityEngine;

namespace Bugsee.Sample
{
    public sealed class Collectible : MonoBehaviour
    {
        public bool IsHazard;
        public System.Action<Collectible> Collected;

        public void Collect()
        {
            Collected?.Invoke(this);
            Destroy(gameObject);
        }

        void OnTriggerEnter(Collider other)
        {
            if (other.GetComponent<AnteaterController>() != null ||
                other.GetComponentInParent<AnteaterController>() != null ||
                other.GetComponent<CharacterController>() != null)
                Collect();
        }
    }

    /// <summary>Sparse field collectibles for Event spam.</summary>
    public sealed class TargetSpawner : MonoBehaviour
    {
        BugseeSampleBootstrap _bootstrap;

        public void Init(BugseeSampleBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
        }

        public void InitField(BugseeSampleBootstrap bootstrap, float halfExtent)
        {
            _bootstrap = bootstrap;
            var rng = new System.Random(21);
            for (int i = 0; i < 10; i++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = 6f + (float)rng.NextDouble() * (halfExtent * 0.55f);
                var pos = new Vector3(Mathf.Cos(angle) * radius, 0.4f, Mathf.Sin(angle) * radius);
                bool hazard = i == 3 || i == 7;
                SpawnAt(pos, hazard);
            }
        }

        void SpawnAt(Vector3 pos, bool hazard)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = hazard ? "Hazard" : "Coin";
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * (hazard ? 0.55f : 0.4f);

            var rend = go.GetComponent<Renderer>();
            rend.sharedMaterial = ProceduralMaterials.CreateLit(
                hazard ? new Color(0.95f, 0.25f, 0.2f) : new Color(1f, 0.85f, 0.2f));

            var col = go.GetComponent<Collider>();
            col.isTrigger = true;

            var c = go.AddComponent<Collectible>();
            c.IsHazard = hazard;
            c.Collected = OnCollected;
        }

        void OnCollected(Collectible c)
        {
            if (_bootstrap == null) return;

            if (c.IsHazard)
            {
                BugseeActionCatalog.Instance?.Run(BugseeDemoAction.LogException);
                _bootstrap.SetStatus("Hazard → LogException");
            }
            else
            {
                _bootstrap.Score += 1;
                Bugsee.Event("field.collect", new Dictionary<string, object>
                {
                    { "score", _bootstrap.Score }
                });
            }
        }
    }
}
