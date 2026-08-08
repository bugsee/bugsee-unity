using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Bugsee.Sample
{
    /// <summary>
    /// Open grassy fields with climbable hills, swaying grass, rocks, and trees.
    /// Each hill summit is an independent Bugsee action waypoint (no maze gating).
    /// </summary>
    public sealed class FieldWorldBuilder : MonoBehaviour
    {
        public const float WorldHalfExtent = 72f;
        public const float HillRadius = 9f;
        /// <summary>Clear grass corridor between dome skirts (center distance = 2*R + gap).</summary>
        public const float HillSkirtGap = 12f;
        /// <summary>Unity cylinder half-height used for the summit pad visual (full height = 2× this).</summary>
        public const float SummitPadHalfHeight = 0.04f;
        /// <summary>World-space Y of the pad top above the dome mesh tip.</summary>
        public const float SummitPadTopOffset = SummitPadHalfHeight * 2f;

        public Vector3 SpawnWorldPos { get; private set; }
        public IReadOnlyList<Vector3> HillSummits => _hillSummits;

        readonly List<Vector3> _hillSummits = new List<Vector3>();

        Material _grassMat;
        Material[] _bladeMats;
        Material[] _barkMats;
        Material[] _canopyMats;
        Material[] _stoneMats;
        Material _stoneMat;
        Mesh _bladeMesh;

        public void Build(int actionCount, int seed = 42)
        {
            SpawnWorldPos = new Vector3(0f, 0f, 0f);
            _hillSummits.Clear();
            SampleQuality.ApplyRuntimeSettings();

            _grassMat = ProceduralMaterials.CreateLit(
                Color.white,
                ProceduralMaterials.NoiseTexture(128, ProceduralMaterials.GrassA, ProceduralMaterials.GrassB, 0.08f, seed));
            // Opaque lit blades with tip wind (chunk transform rotation bobbed the whole patch).
            _bladeMats = new[]
            {
                ProceduralMaterials.CreateWindyLit(new Color(0.32f, 0.62f, 0.24f)),
                ProceduralMaterials.CreateWindyLit(new Color(0.22f, 0.5f, 0.18f)),
                ProceduralMaterials.CreateWindyLit(new Color(0.4f, 0.68f, 0.28f)),
                ProceduralMaterials.CreateWindyLit(new Color(0.3f, 0.55f, 0.2f))
            };

            _barkMats = new[]
            {
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.Bark, ProceduralMaterials.BarkDark, 0.22f, seed + 21)),
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.BarkPale, ProceduralMaterials.Bark, 0.18f, seed + 22)),
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.BarkDark, ProceduralMaterials.BarkPale, 0.26f, seed + 23))
            };
            _canopyMats = new[]
            {
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.Canopy, ProceduralMaterials.CanopyDeep, 0.16f, seed + 31)),
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.CanopyBright, ProceduralMaterials.Canopy, 0.14f, seed + 32)),
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.CanopyOlive, ProceduralMaterials.CanopyDeep, 0.15f, seed + 33)),
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.CanopyDeep, ProceduralMaterials.CanopyOlive, 0.2f, seed + 34))
            };
            _stoneMats = new[]
            {
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.StoneA, ProceduralMaterials.StoneB, 0.14f, seed + 3)),
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.StoneB, ProceduralMaterials.StoneC, 0.18f, seed + 4)),
                ProceduralMaterials.CreateLit(
                    Color.white,
                    ProceduralMaterials.NoiseTexture(64, ProceduralMaterials.StoneC, ProceduralMaterials.StoneA, 0.12f, seed + 5))
            };
            _stoneMat = _stoneMats[0];

            BuildGround();
            BuildHills(actionCount, seed);
            ScatterGrass(seed);
            ScatterRocks(seed);
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
                // Wide enough for the action beacon ring to sit on the flat pad (not over the dome slope).
                // Unity cylinder height = 2*scale.y → top = pos.y + scale.y.
                pad.transform.position = summit + Vector3.up * SummitPadHalfHeight;
                pad.transform.localScale = new Vector3(2.4f, SummitPadHalfHeight, 2.4f);
                // Avoid scaled CapsuleCollider on the visual; use a flat unscaled box platform.
                UnityEngine.Object.Destroy(pad.GetComponent<Collider>());
                pad.GetComponent<Renderer>().sharedMaterial = padMat;
                float padTopY = summit.y + SummitPadTopOffset;

                var padSolid = new GameObject("SummitPadSolid");
                padSolid.transform.SetParent(hill.transform, false);
                // Box top matches the visual pad top exactly.
                padSolid.transform.position = new Vector3(summit.x, padTopY - 0.03f, summit.z);
                padSolid.transform.localScale = Vector3.one;
                var padCol = padSolid.AddComponent<BoxCollider>();
                padCol.size = new Vector3(2.6f, 0.06f, 2.6f);
                padCol.center = Vector3.zero;
                padCol.isTrigger = false;

                // Station spawn points are the pad top (props use local y=0 as feet).
                _hillSummits.Add(new Vector3(summit.x, padTopY, summit.z));
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

        void ScatterGrass(int seed)
        {
            // Combine thin blades into chunk meshes — tens of thousands of Capsule GameObjects
            // destroy mobile frame time; a few dozen combined meshes stay cheap.
            var root = new GameObject("Grass");
            root.transform.SetParent(transform, false);
            var field = root.AddComponent<GrassField>();
            var rng = new System.Random(seed + 77);
            EnsureBladeMesh();

            float step = SampleQuality.GrassStep;
            float chunkSize = SampleQuality.GrassChunkSize;
            var chunks = new Dictionary<long, List<CombineInstance>>(128);
            int bladeCount = 0;

            for (float x = -WorldHalfExtent + 1.5f; x <= WorldHalfExtent - 1.5f; x += step)
            {
                for (float z = -WorldHalfExtent + 1.5f; z <= WorldHalfExtent - 1.5f; z += step)
                {
                    var pos = new Vector3(
                        x + ((float)rng.NextDouble() - 0.5f) * step * 0.85f,
                        0f,
                        z + ((float)rng.NextDouble() - 0.5f) * step * 0.85f);
                    if (pos.sqrMagnitude < 1.6f * 1.6f)
                        continue;
                    if (TooCloseToHill(pos, HillRadius - 0.8f))
                        continue;

                    int ix = Mathf.FloorToInt((pos.x + WorldHalfExtent) / chunkSize);
                    int iz = Mathf.FloorToInt((pos.z + WorldHalfExtent) / chunkSize);
                    long key = ((long)ix << 32) | (uint)iz;
                    if (!chunks.TryGetValue(key, out var list))
                    {
                        list = new List<CombineInstance>(64);
                        chunks[key] = list;
                    }

                    bladeCount += AppendGrassClumpCombines(list, pos, rng);
                }
            }

            var chunkMats = new List<Material>(chunks.Count);
            foreach (var kv in chunks)
            {
                if (kv.Value.Count == 0) continue;

                int ix = (int)(kv.Key >> 32);
                int iz = (int)(kv.Key & 0xffffffff);
                var chunkOrigin = new Vector3(
                    ix * chunkSize - WorldHalfExtent + chunkSize * 0.5f,
                    0f,
                    iz * chunkSize - WorldHalfExtent + chunkSize * 0.5f);

                // Convert world matrices → chunk-local.
                var localCombines = kv.Value;
                var chunkWorld = Matrix4x4.TRS(chunkOrigin, Quaternion.identity, Vector3.one);
                var invChunk = chunkWorld.inverse;
                for (int i = 0; i < localCombines.Count; i++)
                {
                    var ci = localCombines[i];
                    ci.transform = invChunk * ci.transform;
                    localCombines[i] = ci;
                }

                var mesh = new Mesh { name = "GrassChunk", indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(localCombines.ToArray(), true, true);
                mesh.RecalculateBounds();
                // Pad bounds for tip sway (avoids frustum pop when wind displaces verts).
                var b = mesh.bounds;
                b.Expand(0.35f);
                mesh.bounds = b;
                mesh.UploadMeshData(true);

                var chunkGo = new GameObject("GrassChunk_" + ix + "_" + iz);
                chunkGo.transform.SetParent(root.transform, false);
                chunkGo.transform.position = chunkOrigin;
                chunkGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                var rend = chunkGo.AddComponent<MeshRenderer>();
                // Per-chunk instance so patches don't lockstep; tip wind is in the shader.
                float phaseBias = (ix * 0.37f + iz * 0.19f) % 1.7f;
                var mat = new Material(_bladeMats[(ix + iz) % _bladeMats.Length]);
                mat.SetFloat("_WindSpeed", 1.15f + phaseBias * 0.3f);
                mat.SetFloat("_WindAmp", SampleQuality.IsMobile ? 0.11f : 0.15f);
                rend.sharedMaterial = mat;
                rend.shadowCastingMode = ShadowCastingMode.Off;
                rend.receiveShadows = !SampleQuality.IsMobile;
                chunkMats.Add(mat);
            }

            field.Bind(chunkMats.ToArray());
            Debug.Log("[MiniGame] Grass blades=" + bladeCount + " chunks=" + chunkMats.Count +
                      " mobile=" + SampleQuality.IsMobile);
        }

        void EnsureBladeMesh()
        {
            if (_bladeMesh != null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _bladeMesh = Object.Instantiate(tmp.GetComponent<MeshFilter>().sharedMesh);
            _bladeMesh.name = "GrassBladeCapsule";
            Object.Destroy(tmp);
        }

        int AppendGrassClumpCombines(List<CombineInstance> list, Vector3 pos, System.Random rng)
        {
            float scale = 0.75f + (float)rng.NextDouble() * 0.65f;
            int blades = SampleQuality.GrassBladesMin + rng.Next(0, SampleQuality.GrassBladesExtra);
            for (int i = 0; i < blades; i++)
            {
                float h = (0.35f + (float)rng.NextDouble() * 0.45f) * scale;
                float r = (0.0055f + (float)rng.NextDouble() * 0.0045f) * scale;
                float lean = ((float)rng.NextDouble() - 0.5f) * 16f;
                float yaw = (float)rng.NextDouble() * 360f;
                var localPos = pos + new Vector3(
                    ((float)rng.NextDouble() - 0.5f) * 0.14f * scale,
                    h * 0.5f,
                    ((float)rng.NextDouble() - 0.5f) * 0.14f * scale);
                var rot = Quaternion.Euler(lean, yaw, lean * 0.35f);
                var scl = new Vector3(r * 2f, h * 0.5f, r * 2f);
                list.Add(new CombineInstance
                {
                    mesh = _bladeMesh,
                    transform = Matrix4x4.TRS(localPos, rot, scl)
                });
            }

            return blades;
        }

        void ScatterRocks(int seed)
        {
            var root = new GameObject("Rocks");
            root.transform.SetParent(transform, false);
            var rng = new System.Random(seed + 141);
            var placed = new List<Vector3>(90);

            // Lone stones across the field.
            for (int i = 0; i < 70; i++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = 8f + (float)rng.NextDouble() * (WorldHalfExtent - 11f);
                var pos = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                if (!IsDetailSiteOk(pos, spawnClear: 7f) || !FarEnough(pos, placed, 2.2f * 2.2f))
                    continue;
                SpawnRock(root.transform, pos, rng, cluster: false);
                placed.Add(pos);
            }

            // A few rock piles.
            int piles = 5 + rng.Next(0, 4);
            for (int p = 0; p < piles; p++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = 14f + (float)rng.NextDouble() * (WorldHalfExtent - 20f);
                var center = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                if (!IsDetailSiteOk(center, spawnClear: 8f))
                    continue;

                int n = 3 + rng.Next(0, 4);
                for (int i = 0; i < n; i++)
                {
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float r = 0.35f + (float)rng.NextDouble() * 1.4f;
                    var pos = center + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (!IsDetailSiteOk(pos, spawnClear: 7f) || !FarEnough(pos, placed, 0.55f * 0.55f))
                        continue;
                    SpawnRock(root.transform, pos, rng, cluster: true);
                    placed.Add(pos);
                }
            }
        }

        bool IsDetailSiteOk(Vector3 pos, float spawnClear)
        {
            var flat = new Vector3(pos.x, 0f, pos.z);
            if (flat.sqrMagnitude < spawnClear * spawnClear)
                return false;
            if (TooCloseToHill(pos, HillRadius + 1.2f))
                return false;
            return true;
        }

        void SpawnRock(Transform parent, Vector3 pos, System.Random rng, bool cluster)
        {
            var rock = new GameObject(cluster ? "Stone" : "Rock");
            rock.transform.SetParent(parent, false);
            rock.transform.position = pos;
            rock.transform.rotation = Quaternion.Euler(
                (float)rng.NextDouble() * 25f,
                (float)rng.NextDouble() * 360f,
                (float)rng.NextDouble() * 25f);

            float size = cluster
                ? 0.18f + (float)rng.NextDouble() * 0.35f
                : 0.28f + (float)rng.NextDouble() * 0.7f;

            // Mix of chunky shapes.
            PrimitiveType kind = rng.NextDouble() < 0.55 ? PrimitiveType.Sphere : PrimitiveType.Cube;
            var body = GameObject.CreatePrimitive(kind);
            body.name = "Mesh";
            body.transform.SetParent(rock.transform, false);
            body.transform.localPosition = new Vector3(0f, size * 0.35f, 0f);
            body.transform.localScale = new Vector3(
                size * (0.8f + (float)rng.NextDouble() * 0.7f),
                size * (0.55f + (float)rng.NextDouble() * 0.55f),
                size * (0.8f + (float)rng.NextDouble() * 0.7f));
            Object.Destroy(body.GetComponent<Collider>());
            body.GetComponent<Renderer>().sharedMaterial = _stoneMats[rng.Next(_stoneMats.Length)];

            // Unscaled solid so CharacterController bumps instead of clipping.
            var solid = rock.AddComponent<SphereCollider>();
            solid.radius = size * 0.55f;
            solid.center = new Vector3(0f, size * 0.35f, 0f);
            solid.isTrigger = false;
        }

        void ScatterTrees(int seed)
        {
            var forest = new GameObject("Trees");
            forest.transform.SetParent(transform, false);
            var rng = new System.Random(seed + 99);
            var placed = new List<Vector3>(220);
            const float spawnClear = 10f;
            const float minTreeDist = 2.6f;
            float minTreeSqr = minTreeDist * minTreeDist;

            // Open-field scatter.
            TryPlaceTrees(forest.transform, placed, rng, attempts: SampleQuality.IsMobile ? 120 : 220,
                want: SampleQuality.TreeFieldWant,
                minRadius: spawnClear, maxRadius: WorldHalfExtent - 3f, minTreeSqr);

            // Small groves for denser pockets.
            int groveCount = SampleQuality.IsMobile ? 4 + rng.Next(0, 3) : 7 + rng.Next(0, 4);
            for (int g = 0; g < groveCount; g++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = 16f + (float)rng.NextDouble() * (WorldHalfExtent - 22f);
                var center = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                if (!IsTreeSiteOk(center, spawnClear) || !FarEnough(center, placed, minTreeSqr * 0.35f))
                    continue;

                int members = 4 + rng.Next(0, 5);
                for (int m = 0; m < members; m++)
                {
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float r = 1.2f + (float)rng.NextDouble() * 4.5f;
                    var pos = center + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (!TryAcceptTreeSite(pos, placed, spawnClear, minTreeSqr))
                        continue;
                    SpawnTree(forest.transform, pos, rng);
                    placed.Add(pos);
                }
            }

            // Soft outer ring for horizon read.
            int ringCount = SampleQuality.TreeOuterRing;
            for (int i = 0; i < ringCount; i++)
            {
                float angle = (i + (float)rng.NextDouble() * 0.7f) / ringCount * Mathf.PI * 2f;
                float radius = WorldHalfExtent + 1.2f + (float)rng.NextDouble() * 7f;
                var pos = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                // Outer ring may sit outside playable area — skip hill/spawn checks.
                if (!FarEnough(pos, placed, minTreeSqr * 0.55f))
                    continue;
                SpawnTree(forest.transform, pos, rng);
                placed.Add(pos);
            }
        }

        void TryPlaceTrees(
            Transform parent,
            List<Vector3> placed,
            System.Random rng,
            int attempts,
            int want,
            float minRadius,
            float maxRadius,
            float minTreeSqr)
        {
            int added = 0;
            for (int i = 0; i < attempts && added < want; i++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = minRadius + (float)rng.NextDouble() * (maxRadius - minRadius);
                var pos = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                if (!TryAcceptTreeSite(pos, placed, minRadius, minTreeSqr))
                    continue;
                SpawnTree(parent, pos, rng);
                placed.Add(pos);
                added++;
            }
        }

        bool TryAcceptTreeSite(Vector3 pos, List<Vector3> placed, float spawnClear, float minTreeSqr)
        {
            if (!IsTreeSiteOk(pos, spawnClear))
                return false;
            return FarEnough(pos, placed, minTreeSqr);
        }

        bool IsTreeSiteOk(Vector3 pos, float spawnClear)
        {
            var flat = new Vector3(pos.x, 0f, pos.z);
            if (flat.sqrMagnitude < spawnClear * spawnClear)
                return false;
            // Keep trees off the dome skirts and summit pads.
            if (TooCloseToHill(pos, HillRadius + 2.5f))
                return false;
            return true;
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

        enum TreeKind
        {
            Broadleaf,
            Oak,
            Pine
        }

        void SpawnTree(Transform parent, Vector3 pos, System.Random rng)
        {
            var kindRoll = rng.NextDouble();
            var kind = kindRoll < 0.55 ? TreeKind.Broadleaf
                : kindRoll < 0.82 ? TreeKind.Oak
                : TreeKind.Pine;

            var tree = new GameObject(kind == TreeKind.Pine ? "Pine" : kind == TreeKind.Oak ? "Oak" : "Tree");
            tree.transform.SetParent(parent, false);
            tree.transform.position = pos;
            tree.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

            var bark = _barkMats[rng.Next(_barkMats.Length)];
            var canopyMat = _canopyMats[rng.Next(_canopyMats.Length)];

            switch (kind)
            {
                case TreeKind.Pine:
                    SpawnPine(tree.transform, bark, canopyMat, rng);
                    break;
                case TreeKind.Oak:
                    SpawnOak(tree.transform, bark, canopyMat, rng);
                    break;
                default:
                    SpawnBroadleaf(tree.transform, bark, canopyMat, rng);
                    break;
            }
        }

        void SpawnBroadleaf(Transform root, Material bark, Material canopyMat, System.Random rng)
        {
            float scale = 0.85f + (float)rng.NextDouble() * 0.55f;
            float trunkH = (2.0f + (float)rng.NextDouble() * 1.8f) * scale;
            float trunkR = (0.14f + (float)rng.NextDouble() * 0.1f) * scale;
            float lean = ((float)rng.NextDouble() - 0.5f) * 8f;

            BuildTrunk(root, bark, trunkH, trunkR, lean, flare: true);

            var foliage = new GameObject("Foliage");
            foliage.transform.SetParent(root, false);
            foliage.transform.localPosition = new Vector3(0f, trunkH * 0.92f, 0f);

            int lobes = 3 + rng.Next(0, 3);
            float baseS = (1.15f + (float)rng.NextDouble() * 0.85f) * scale;
            for (int i = 0; i < lobes; i++)
            {
                float a = i * (Mathf.PI * 2f / lobes) + (float)rng.NextDouble() * 0.4f;
                float radial = baseS * (0.18f + (float)rng.NextDouble() * 0.28f);
                float y = baseS * (0.05f + (float)rng.NextDouble() * 0.35f);
                float s = baseS * (0.55f + (float)rng.NextDouble() * 0.5f);
                AddCanopyLobe(foliage.transform, canopyMat,
                    new Vector3(Mathf.Cos(a) * radial, y, Mathf.Sin(a) * radial),
                    new Vector3(s, s * (0.7f + (float)rng.NextDouble() * 0.25f), s));
            }

            // Crown tip
            AddCanopyLobe(foliage.transform, canopyMat,
                new Vector3(0f, baseS * 0.55f, 0f),
                new Vector3(baseS * 0.7f, baseS * 0.65f, baseS * 0.7f));

            MaybeAddBranch(root, foliage.transform, bark, canopyMat, trunkH, trunkR, scale, rng);
            foliage.AddComponent<TreeSway>().Configure(
                0.55f + (float)rng.NextDouble() * 0.9f,
                1.6f + (float)rng.NextDouble() * 1.8f);
        }

        void SpawnOak(Transform root, Material bark, Material canopyMat, System.Random rng)
        {
            float scale = 0.95f + (float)rng.NextDouble() * 0.5f;
            float trunkH = (1.7f + (float)rng.NextDouble() * 1.1f) * scale;
            float trunkR = (0.22f + (float)rng.NextDouble() * 0.14f) * scale;
            float lean = ((float)rng.NextDouble() - 0.5f) * 6f;

            BuildTrunk(root, bark, trunkH, trunkR, lean, flare: true);

            var foliage = new GameObject("Foliage");
            foliage.transform.SetParent(root, false);
            foliage.transform.localPosition = new Vector3(0f, trunkH * 0.88f, 0f);

            float wide = (1.7f + (float)rng.NextDouble() * 0.9f) * scale;
            int lobes = 5 + rng.Next(0, 3);
            for (int i = 0; i < lobes; i++)
            {
                float a = i * (Mathf.PI * 2f / lobes) + (float)rng.NextDouble() * 0.5f;
                float radial = wide * (0.22f + (float)rng.NextDouble() * 0.35f);
                float y = wide * ((float)rng.NextDouble() * 0.22f - 0.05f);
                float sx = wide * (0.45f + (float)rng.NextDouble() * 0.4f);
                float sy = wide * (0.32f + (float)rng.NextDouble() * 0.22f);
                AddCanopyLobe(foliage.transform, canopyMat,
                    new Vector3(Mathf.Cos(a) * radial, y, Mathf.Sin(a) * radial),
                    new Vector3(sx, sy, sx));
            }

            AddCanopyLobe(foliage.transform, canopyMat,
                new Vector3(0f, wide * 0.18f, 0f),
                new Vector3(wide * 0.85f, wide * 0.45f, wide * 0.85f));

            MaybeAddBranch(root, foliage.transform, bark, canopyMat, trunkH, trunkR, scale, rng);
            MaybeAddBranch(root, foliage.transform, bark, canopyMat, trunkH, trunkR, scale, rng);
            foliage.AddComponent<TreeSway>().Configure(
                0.4f + (float)rng.NextDouble() * 0.7f,
                1.2f + (float)rng.NextDouble() * 1.4f);
        }

        void SpawnPine(Transform root, Material bark, Material canopyMat, System.Random rng)
        {
            float scale = 0.9f + (float)rng.NextDouble() * 0.7f;
            float trunkH = (2.6f + (float)rng.NextDouble() * 2.2f) * scale;
            float trunkR = (0.12f + (float)rng.NextDouble() * 0.08f) * scale;
            float lean = ((float)rng.NextDouble() - 0.5f) * 4f;

            BuildTrunk(root, bark, trunkH, trunkR, lean, flare: false);

            var foliage = new GameObject("Foliage");
            foliage.transform.SetParent(root, false);
            foliage.transform.localPosition = Vector3.zero;

            int tiers = 3 + rng.Next(0, 3);
            float top = trunkH * (0.92f + (float)rng.NextDouble() * 0.08f);
            for (int i = 0; i < tiers; i++)
            {
                float t = i / (float)Mathf.Max(1, tiers - 1);
                float y = top - t * trunkH * 0.55f;
                float width = (0.55f + t * 1.15f) * scale * (0.85f + (float)rng.NextDouble() * 0.25f);
                float height = (0.55f + (1f - t) * 0.35f) * scale;
                AddCanopyLobe(foliage.transform, canopyMat,
                    new Vector3(0f, y, 0f),
                    new Vector3(width, height, width));
            }

            foliage.AddComponent<TreeSway>().Configure(
                0.35f + (float)rng.NextDouble() * 0.55f,
                0.9f + (float)rng.NextDouble() * 1.1f);
        }

        void BuildTrunk(Transform root, Material bark, float height, float radius, float leanDeg, bool flare)
        {
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.SetParent(root, false);
            trunk.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
            trunk.transform.localRotation = Quaternion.Euler(leanDeg * 0.35f, 0f, leanDeg);
            // Slight taper: thinner toward the top via non-uniform isn't free on a cylinder,
            // so scale XZ a bit smaller and rely on flare for base mass.
            trunk.transform.localScale = new Vector3(radius * 1.85f, height * 0.5f, radius * 1.85f);
            Object.Destroy(trunk.GetComponent<Collider>());
            trunk.GetComponent<Renderer>().sharedMaterial = bark;

            if (flare)
            {
                var baseFlare = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                baseFlare.name = "RootFlare";
                baseFlare.transform.SetParent(root, false);
                baseFlare.transform.localPosition = new Vector3(0f, radius * 0.55f, 0f);
                baseFlare.transform.localScale = new Vector3(radius * 3.2f, radius * 1.4f, radius * 3.2f);
                Object.Destroy(baseFlare.GetComponent<Collider>());
                baseFlare.GetComponent<Renderer>().sharedMaterial = bark;
            }

            // Upright gameplay capsule (visual trunk may lean; CC needs a stable solid).
            var solid = root.GetComponent<CapsuleCollider>();
            if (solid == null)
                solid = root.gameObject.AddComponent<CapsuleCollider>();
            float padR = Mathf.Max(0.22f, radius * (flare ? 1.75f : 1.35f));
            solid.radius = padR;
            solid.height = Mathf.Max(height * 0.95f, padR * 2f + 0.05f);
            solid.center = new Vector3(0f, solid.height * 0.5f, 0f);
            solid.direction = 1;
            solid.isTrigger = false;
        }

        void MaybeAddBranch(
            Transform root,
            Transform foliage,
            Material bark,
            Material canopyMat,
            float trunkH,
            float trunkR,
            float scale,
            System.Random rng)
        {
            if (rng.NextDouble() > 0.72)
                return;

            float y = trunkH * (0.45f + (float)rng.NextDouble() * 0.3f);
            float yaw = (float)rng.NextDouble() * 360f;
            float pitch = 25f + (float)rng.NextDouble() * 35f;
            float len = (0.55f + (float)rng.NextDouble() * 0.7f) * scale;
            float br = trunkR * (0.35f + (float)rng.NextDouble() * 0.25f);

            var branch = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            branch.name = "Branch";
            branch.transform.SetParent(root, false);
            branch.transform.localPosition = new Vector3(0f, y, 0f);
            branch.transform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            branch.transform.localScale = new Vector3(br * 2f, len * 0.5f, br * 2f);
            // Cylinder extends along local Y — shift so it grows outward from the trunk.
            branch.transform.Translate(0f, len * 0.5f, 0f, Space.Self);
            Object.Destroy(branch.GetComponent<Collider>());
            branch.GetComponent<Renderer>().sharedMaterial = bark;

            // Unit cylinder spans local Y -1..1; after scale/translate the +Y pole is the tip.
            var tip = branch.transform.TransformPoint(new Vector3(0f, 0.9f, 0f));
            var localTip = foliage.InverseTransformPoint(tip);
            float clump = (0.45f + (float)rng.NextDouble() * 0.35f) * scale;
            AddCanopyLobe(foliage, canopyMat, localTip, new Vector3(clump, clump * 0.75f, clump));
        }

        static void AddCanopyLobe(Transform parent, Material mat, Vector3 localPos, Vector3 localScale)
        {
            var lobe = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lobe.name = "Canopy";
            lobe.transform.SetParent(parent, false);
            lobe.transform.localPosition = localPos;
            lobe.transform.localScale = localScale;
            Object.Destroy(lobe.GetComponent<Collider>());
            lobe.GetComponent<Renderer>().sharedMaterial = mat;
        }
    }
}
