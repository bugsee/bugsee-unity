using System.Collections.Generic;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Open grassy fields with climbable hills and scattered trees.
    /// Each hill summit is an independent Bugsee action waypoint (no maze gating).
    /// </summary>
    public sealed class FieldWorldBuilder : MonoBehaviour
    {
        public const float WorldHalfExtent = 72f;
        public const float HillRadius = 9f;
        /// <summary>Clear grass corridor between dome skirts (center distance = 2*R + gap).</summary>
        public const float HillSkirtGap = 12f;

        public Vector3 SpawnWorldPos { get; private set; }
        public IReadOnlyList<Vector3> HillSummits => _hillSummits;

        readonly List<Vector3> _hillSummits = new List<Vector3>();

        Material _grassMat;
        Material _barkMat;
        Material _canopyMat;
        Material _stoneMat;

        public void Build(int actionCount, int seed = 42)
        {
            SpawnWorldPos = new Vector3(0f, 0.05f, 0f);
            _hillSummits.Clear();

            _grassMat = ProceduralMaterials.CreateLit(
                Color.white,
                ProceduralMaterials.NoiseTexture(128, ProceduralMaterials.GrassA, ProceduralMaterials.GrassB, 0.08f, seed));
            _barkMat = ProceduralMaterials.CreateLit(ProceduralMaterials.Bark);
            _canopyMat = ProceduralMaterials.CreateLit(ProceduralMaterials.Canopy);
            _stoneMat = ProceduralMaterials.CreateLit(
                Color.white,
                ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.StoneA, ProceduralMaterials.StoneB, 0.14f, seed + 3));

            BuildGround();
            BuildHills(actionCount, seed);
            ScatterTrees(seed);
            BuildPerimeterRing();
        }

        void BuildGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "FieldGround";
            ground.transform.SetParent(transform, false);
            // Unity plane is 10×10 at scale 1.
            ground.transform.localScale = new Vector3(WorldHalfExtent / 5f, 1f, WorldHalfExtent / 5f);
            ground.GetComponent<Renderer>().sharedMaterial = _grassMat;
        }

        void BuildHills(int actionCount, int seed)
        {
            var hills = new GameObject("Hills");
            hills.transform.SetParent(transform, false);
            var rng = new System.Random(seed + 11);
            var padMat = ProceduralMaterials.CreateLit(
                Color.Lerp(ProceduralMaterials.Path, ProceduralMaterials.GrassA, 0.35f));

            float minCenterDist = HillRadius * 2f + HillSkirtGap;
            var bases = LayoutHillBases(actionCount, rng, HillRadius, minCenterDist);
            for (int i = 0; i < bases.Count; i++)
            {
                var basePos = bases[i];
                // Consistent radius so separation math matches the mesh.
                float radius = HillRadius;
                float height = 2.1f + (float)rng.NextDouble() * 0.35f;

                var hill = new GameObject("Hill_" + i);
                hill.transform.SetParent(hills.transform, false);
                hill.transform.position = basePos;

                var mesh = BuildDomeMesh(radius, height, rings: 10, sectors: 28);
                var filter = hill.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var rend = hill.AddComponent<MeshRenderer>();
                rend.sharedMaterial = _grassMat;
                var col = hill.AddComponent<MeshCollider>();
                col.sharedMesh = mesh;

                var summit = basePos + Vector3.up * height;
                var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pad.name = "SummitPad";
                pad.transform.SetParent(hill.transform, false);
                pad.transform.position = summit + Vector3.up * 0.03f;
                pad.transform.localScale = new Vector3(1.6f, 0.04f, 1.6f);
                UnityEngine.Object.Destroy(pad.GetComponent<Collider>());
                pad.GetComponent<Renderer>().sharedMaterial = padMat;

                _hillSummits.Add(summit + Vector3.up * 0.08f);
            }
        }

        /// <summary>
        /// Smooth dome: y = height * (1 - (r/radius)^2). Edge gradient = 2*height/radius
        /// (~22° for h=2, r=10) so CharacterController can walk up without jumping.
        /// </summary>
        static Mesh BuildDomeMesh(float radius, float height, int rings, int sectors)
        {
            rings = Mathf.Max(3, rings);
            sectors = Mathf.Max(8, sectors);

            int vertCount = 1 + rings * sectors;
            var verts = new Vector3[vertCount];
            var normals = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];

            verts[0] = new Vector3(0f, height, 0f);
            normals[0] = Vector3.up;
            uvs[0] = new Vector2(0.5f, 0.5f);

            int v = 1;
            for (int ring = 1; ring <= rings; ring++)
            {
                float t = ring / (float)rings;
                float r = radius * t;
                // Quadratic falloff — flat near summit, gentle toward the rim.
                float y = height * (1f - t * t);
                if (ring == rings)
                    y = 0f;

                for (int s = 0; s < sectors; s++)
                {
                    float a = (s / (float)sectors) * Mathf.PI * 2f;
                    float x = Mathf.Cos(a) * r;
                    float z = Mathf.Sin(a) * r;
                    verts[v] = new Vector3(x, y, z);
                    uvs[v] = new Vector2(x / (radius * 2f) + 0.5f, z / (radius * 2f) + 0.5f);

                    // Analytic normal for y = h*(1 - (x^2+z^2)/R^2): n ~ (-dy/dx, 1, -dy/dz)
                    float dyScale = 2f * height / (radius * radius);
                    var n = new Vector3(x * dyScale, 1f, z * dyScale).normalized;
                    normals[v] = n;
                    v++;
                }
            }

            // Triangles: tip fan + ring strips.
            var tris = new List<int>(sectors * rings * 6);
            for (int s = 0; s < sectors; s++)
            {
                int cur = 1 + s;
                int next = 1 + (s + 1) % sectors;
                tris.Add(0);
                tris.Add(cur);
                tris.Add(next);
            }

            for (int ring = 1; ring < rings; ring++)
            {
                int row = 1 + (ring - 1) * sectors;
                int nextRow = 1 + ring * sectors;
                for (int s = 0; s < sectors; s++)
                {
                    int s2 = (s + 1) % sectors;
                    int a = row + s;
                    int b = row + s2;
                    int c = nextRow + s;
                    int d = nextRow + s2;
                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(d);
                    tris.Add(a);
                    tris.Add(d);
                    tris.Add(c);
                }
            }

            var mesh = new Mesh { name = "HillDome" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static List<Vector3> LayoutHillBases(int count, System.Random rng, float hillRadius, float minCenterDist)
        {
            var list = new List<Vector3>(count);
            if (count <= 0) return list;

            float spawnClear = Mathf.Max(18f, hillRadius + 10f);
            float maxR = WorldHalfExtent - hillRadius - 4f;
            float minSqr = minCenterDist * minCenterDist;

            // Even rings sized so neighbors on the same ring stay >= minCenterDist apart.
            int ring = 0;
            while (list.Count < count && ring < 12)
            {
                ring++;
                // Radial step ≈ minCenterDist so consecutive rings don't merge.
                float radius = spawnClear + (ring - 1) * minCenterDist;
                if (radius > maxR)
                    break;

                int maxOnRing = Mathf.Max(1, Mathf.FloorToInt((Mathf.PI * 2f * radius) / minCenterDist));
                int slots = Mathf.Min(count - list.Count, maxOnRing);
                // Prefer not packing the ring completely full — leave a bit of slack.
                if (slots > 2)
                    slots = Mathf.Max(2, slots - (ring == 1 ? 0 : 1));

                float start = (float)rng.NextDouble() * 0.35f + ring * 0.55f;
                for (int i = 0; i < slots && list.Count < count; i++)
                {
                    float a = start + i * (Mathf.PI * 2f / slots);
                    var p = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                    if (FarEnough(p, list, minSqr))
                        list.Add(p);
                }
            }

            // Spiral fallback for any remaining (should be rare with larger field).
            float spiralR = spawnClear;
            int guard = 0;
            while (list.Count < count && guard++ < 400)
            {
                spiralR += minCenterDist * 0.35f;
                if (spiralR > maxR)
                    spiralR = spawnClear + (float)rng.NextDouble() * (maxR - spawnClear);

                float a = guard * 1.7f + (float)rng.NextDouble();
                var p = new Vector3(Mathf.Cos(a) * spiralR, 0f, Mathf.Sin(a) * spiralR);
                if (p.magnitude > maxR) continue;
                if (FarEnough(p, list, minSqr))
                    list.Add(p);
            }

            return list;
        }

        static bool FarEnough(Vector3 candidate, List<Vector3> placed, float minSqr)
        {
            for (int i = 0; i < placed.Count; i++)
            {
                var d = candidate - placed[i];
                d.y = 0f;
                if (d.sqrMagnitude < minSqr)
                    return false;
            }

            return true;
        }

        void ScatterTrees(int seed)
        {
            var forest = new GameObject("Trees");
            forest.transform.SetParent(transform, false);
            var rng = new System.Random(seed + 99);

            // Sparse field trees — avoid spawn plaza and hill centers.
            for (int i = 0; i < 36; i++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = 8f + (float)rng.NextDouble() * (WorldHalfExtent - 12f);
                var pos = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                // Keep trees off the dome skirts.
                if (TooCloseToHill(pos, HillRadius + 3f)) continue;
                SpawnTree(forest.transform, pos, rng);
            }

            // Soft outer ring for horizon read.
            for (int i = 0; i < 40; i++)
            {
                float angle = (float)i / 40f * Mathf.PI * 2f;
                float radius = WorldHalfExtent + 1.5f + (float)rng.NextDouble() * 5f;
                var pos = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                SpawnTree(forest.transform, pos, rng);
            }
        }

        bool TooCloseToHill(Vector3 pos, float minDist)
        {
            float minSqr = minDist * minDist;
            for (int i = 0; i < _hillSummits.Count; i++)
            {
                var h = _hillSummits[i];
                var d = new Vector3(pos.x - h.x, 0f, pos.z - h.z);
                if (d.sqrMagnitude < minSqr) return true;
            }

            return false;
        }

        void BuildPerimeterRing()
        {
            var border = new GameObject("Border");
            border.transform.SetParent(transform, false);
            float extent = WorldHalfExtent + 1.5f;
            float thick = 2f;
            float h = 3f;
            CreateBorderWall(border.transform, new Vector3(0f, h * 0.5f, extent), new Vector3(extent * 2f + thick, h, thick));
            CreateBorderWall(border.transform, new Vector3(0f, h * 0.5f, -extent), new Vector3(extent * 2f + thick, h, thick));
            CreateBorderWall(border.transform, new Vector3(extent, h * 0.5f, 0f), new Vector3(thick, h, extent * 2f));
            CreateBorderWall(border.transform, new Vector3(-extent, h * 0.5f, 0f), new Vector3(thick, h, extent * 2f));
        }

        void CreateBorderWall(Transform parent, Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "BorderWall";
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = _stoneMat;
        }

        void SpawnTree(Transform parent, Vector3 pos, System.Random rng)
        {
            var tree = new GameObject("Tree");
            tree.transform.SetParent(parent, false);
            tree.transform.position = pos;

            float trunkH = 1.6f + (float)rng.NextDouble() * 1.4f;
            float trunkR = 0.18f + (float)rng.NextDouble() * 0.12f;
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.SetParent(tree.transform, false);
            trunk.transform.localPosition = new Vector3(0f, trunkH * 0.5f, 0f);
            trunk.transform.localScale = new Vector3(trunkR * 2f, trunkH * 0.5f, trunkR * 2f);
            Object.Destroy(trunk.GetComponent<Collider>());
            trunk.GetComponent<Renderer>().sharedMaterial = _barkMat;

            var canopy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            canopy.name = "Canopy";
            canopy.transform.SetParent(tree.transform, false);
            float canopyS = 1.2f + (float)rng.NextDouble() * 1.1f;
            canopy.transform.localPosition = new Vector3(0f, trunkH + canopyS * 0.25f, 0f);
            canopy.transform.localScale = new Vector3(canopyS, canopyS * 0.85f, canopyS);
            Object.Destroy(canopy.GetComponent<Collider>());
            canopy.GetComponent<Renderer>().sharedMaterial = _canopyMat;

            tree.AddComponent<TreeSway>().Configure(0.8f + (float)rng.NextDouble() * 1.4f);
        }
    }
}
