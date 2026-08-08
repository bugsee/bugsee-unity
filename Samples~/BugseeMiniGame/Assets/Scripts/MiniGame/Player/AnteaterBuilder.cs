using System.Collections.Generic;
using UnityEngine;

namespace Bugsee.Sample
{
    /// <summary>
    /// Builds the player anteater: prefers Meshy rigged FBX (Animator walk),
    /// then static remeshed mesh, then procedural organic meshes. See Art/Anteater/LICENSE.txt.
    /// </summary>
    public static class AnteaterBuilder
    {
        const string RiggedResourcePath = "Anteater/ScarletSnout";
        const string RiggedWalkClipPath = "Anteater/ScarletSnoutWalk";
        const string GeneratedResourcePath = "Anteater/AnteaterMesh";

        static readonly Color FurCoral = new Color(0.86f, 0.38f, 0.34f, 1f);
        static readonly Color FurBrown = new Color(0.42f, 0.26f, 0.18f, 1f);
        static readonly Color FurDark = new Color(0.28f, 0.16f, 0.12f, 1f);
        static readonly Color SnoutPink = new Color(0.78f, 0.42f, 0.40f, 1f);

        public static GameObject Build(Transform parent = null)
        {
            var rigged = TryBuildRigged(parent);
            if (rigged != null)
                return rigged;

            var generated = TryBuildStaticMesh(parent);
            if (generated != null)
                return generated;

            return BuildProcedural(parent);
        }

        /// <summary>
        /// Instantiate Meshy quadruped FBX with Walk. CharacterController stays authoritative.
        /// Samples the FBX AnimationClip directly each frame (no Legacy conversion, no Mecanim
        /// controller — both are fragile across Editor vs device / missing .anim refs).
        /// </summary>
        static GameObject TryBuildRigged(Transform parent)
        {
            var prefab = Resources.Load<GameObject>(RiggedResourcePath);
            if (prefab == null)
                return null;

            var walkClip = FindWalkClip();
            if (walkClip == null)
                Debug.LogWarning("[Anteater] ScarletSnout FBX loaded but no AnimationClip found.");

            var root = new GameObject("Anteater");
            if (parent != null)
                root.transform.SetParent(parent, false);

            var instance = UnityEngine.Object.Instantiate(prefab, root.transform, false);
            instance.name = "ScarletSnout";

            // Imported FBX may carry mesh colliders — locomotion uses CharacterController only.
            var colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                UnityEngine.Object.Destroy(colliders[i]);

            ApplyCoralMaterials(instance);

            // Disable components that would fight SampleAnimation.
            var animators = instance.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                animators[i].applyRootMotion = false;
                animators[i].enabled = false;
            }

            var legacyAnims = instance.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < legacyAnims.Length; i++)
                legacyAnims[i].enabled = false;

            if (walkClip != null)
                walkClip.SampleAnimation(instance, 0f);

            // Ground after a walk pose is applied (walk takes often lift the hips vs bind pose).
            FitRiggedInstance(instance.transform, walkClip);
            AnteaterGroundAlign.Attach(root.transform, instance.transform);

            var marker = root.AddComponent<AnteaterGeneratedVisual>();
            marker.Configure(walkClip, instance, hasSkinnedWalk: walkClip != null);
            return root;
        }

        static AnimationClip FindWalkClip()
        {
            // Prefer editor-baked looping .anim when present.
            var baked = Resources.Load<AnimationClip>(RiggedWalkClipPath);
            if (baked != null)
                return baked;

            var clips = Resources.LoadAll<AnimationClip>("Anteater");
            AnimationClip fallback = null;
            for (int i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];
                if (clip == null) continue;
                if (clip.name.StartsWith("__preview__", System.StringComparison.Ordinal))
                    continue;
                if (clip.name.IndexOf("AnteaterMesh", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (clip.name.IndexOf("walk", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    clip.name.IndexOf("ScarletSnout", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    clip.name.IndexOf("Armature", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    clip.name.IndexOf("Take", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return clip;
                if (fallback == null)
                    fallback = clip;
            }

            return fallback;
        }

        static void ApplyCoralMaterials(GameObject instance)
        {
            // Near-solid coral — noisy albedo reads as shadow acne on this mesh.
            var mat = ProceduralMaterials.CreateLit(FurCoral);
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var rend = renderers[i];
                if (rend == null) continue;
                rend.sharedMaterial = mat;
                // Cast real silhouette; never receive (self-shadow acne on dense Meshy skin).
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                rend.receiveShadows = false;

                var skin = rend as SkinnedMeshRenderer;
                if (skin != null)
                {
                    skin.updateWhenOffscreen = true;
                    skin.skinnedMotionVectors = false;
                }
            }
        }

        static void FitRiggedInstance(Transform instance, AnimationClip walkClip = null)
        {
            // Measure bounds in root-local space after identity placement.
            instance.localPosition = Vector3.zero;
            instance.localRotation = Quaternion.identity;
            instance.localScale = Vector3.one;

            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return;

            if (walkClip != null)
                walkClip.SampleAnimation(instance.gameObject, 0f);

            Bounds b = EncapsulateRendererBounds(renderers);

            const float targetHeight = 0.9f;
            float height = b.size.y;
            if (height > 0.01f)
                instance.localScale = Vector3.one * (targetHeight / height);

            // Find the lowest mesh point across the walk cycle so a lifted hip pose
            // does not leave the character hovering above the CharacterController feet.
            float minFeetLocalY = float.MaxValue;
            var parent = instance.parent;
            if (walkClip != null && walkClip.length > 0.01f)
            {
                const int samples = 10;
                for (int s = 0; s < samples; s++)
                {
                    walkClip.SampleAnimation(instance.gameObject, walkClip.length * (s / (float)samples));
                    float feet = FeetLocalY(instance, parent);
                    if (feet < minFeetLocalY)
                        minFeetLocalY = feet;
                }

                walkClip.SampleAnimation(instance.gameObject, 0f);
            }
            else
            {
                minFeetLocalY = FeetLocalY(instance, parent);
            }

            var lp = instance.localPosition;
            lp.y -= minFeetLocalY;
            // Coarse place; AnteaterGroundAlign does the precise per-frame snap.
            lp.y -= 0.08f;
            instance.localPosition = lp;
        }

        static Bounds EncapsulateRendererBounds(Renderer[] renderers)
        {
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    b.Encapsulate(renderers[i].bounds);
            }

            return b;
        }

        static float FeetLocalY(Transform instance, Transform parent)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return 0f;

            Bounds b = EncapsulateRendererBounds(renderers);
            if (parent == null)
                return b.min.y;

            return parent.InverseTransformPoint(new Vector3(b.center.x, b.min.y, b.center.z)).y;
        }

        /// <summary>Static remeshed OBJ path (no skin). Used if rigged FBX is missing.</summary>
        static GameObject TryBuildStaticMesh(Transform parent)
        {
            var mesh = FindGeneratedMesh();
            if (mesh == null)
                return null;

            var root = new GameObject("Anteater");
            if (parent != null)
                root.transform.SetParent(parent, false);

            var visual = new GameObject("GeneratedMesh");
            visual.transform.SetParent(root.transform, false);
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one * 1.15f;

            var mf = visual.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = visual.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ProceduralMaterials.CreateLit(
                Color.white,
                ProceduralMaterials.NoiseTexture(64, FurCoral, FurBrown, 0.2f, 17));

            var marker = root.AddComponent<AnteaterGeneratedVisual>();
            marker.Configure(null, null, hasSkinnedWalk: false);
            return root;
        }

        static Mesh FindGeneratedMesh()
        {
            // Prefer an explicit Mesh sub-asset / standalone mesh.
            var direct = Resources.Load<Mesh>(GeneratedResourcePath);
            if (direct != null)
                return direct;

            var meshes = Resources.LoadAll<Mesh>("Anteater");
            for (int i = 0; i < meshes.Length; i++)
            {
                var m = meshes[i];
                if (m == null) continue;
                if (m.name == "AnteaterMesh" || m.name.IndexOf("AnteaterMesh", System.StringComparison.Ordinal) >= 0)
                    return m;
            }

            // OBJ ModelImporter often exposes a root GameObject with MeshFilter children.
            var go = Resources.Load<GameObject>(GeneratedResourcePath);
            if (go != null)
            {
                var filters = go.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < filters.Length; i++)
                {
                    if (filters[i] != null && filters[i].sharedMesh != null)
                        return filters[i].sharedMesh;
                }
            }

            return null;
        }

        static GameObject BuildProcedural(Transform parent)
        {
            var root = new GameObject("Anteater");
            if (parent != null)
                root.transform.SetParent(parent, false);

            var furMat = ProceduralMaterials.CreateLit(
                Color.white,
                ProceduralMaterials.NoiseTexture(64, FurCoral, FurBrown, 0.18f, 17));
            var bellyMat = ProceduralMaterials.CreateLit(FurBrown);
            var snoutMat = ProceduralMaterials.CreateLit(SnoutPink);
            var darkMat = ProceduralMaterials.CreateLit(FurDark);
            var eyeWhite = ProceduralMaterials.CreateLit(Color.white);
            var pupilMat = ProceduralMaterials.CreateLit(new Color(0.12f, 0.08f, 0.08f));

            // Torso — long low body (forward = +Z).
            var torso = CreateMeshPart(root.transform, "Torso", BuildBodyMesh(), furMat);
            torso.transform.localPosition = new Vector3(0f, 0.48f, -0.05f);

            var belly = CreateMeshPart(root.transform, "Belly", BuildEllipsoidMesh(0.28f, 0.18f, 0.42f, 12, 10), bellyMat);
            belly.transform.localPosition = new Vector3(0f, 0.32f, -0.05f);

            // Neck / shoulders swell
            var shoulder = CreateMeshPart(root.transform, "Shoulder", BuildEllipsoidMesh(0.26f, 0.22f, 0.22f, 12, 10), furMat);
            shoulder.transform.localPosition = new Vector3(0f, 0.52f, 0.32f);

            // Head
            var head = CreateMeshPart(root.transform, "Head", BuildEllipsoidMesh(0.18f, 0.16f, 0.2f, 12, 10), furMat);
            head.transform.localPosition = new Vector3(0f, 0.58f, 0.52f);

            // Long tapered snout (lathed)
            var snout = CreateMeshPart(root.transform, "Snout", BuildSnoutMesh(), snoutMat);
            snout.transform.localPosition = new Vector3(0f, 0.42f, 0.62f);
            snout.transform.localRotation = Quaternion.Euler(28f, 0f, 0f);

            var nose = CreateMeshPart(root.transform, "Nose", BuildEllipsoidMesh(0.06f, 0.05f, 0.05f, 8, 6), darkMat);
            nose.transform.localPosition = new Vector3(0f, 0.22f, 1.18f);

            // Ears
            CreateEar(root.transform, furMat, new Vector3(-0.12f, 0.74f, 0.48f), -18f);
            CreateEar(root.transform, furMat, new Vector3(0.12f, 0.74f, 0.48f), 18f);

            // Eyes
            CreateEye(root.transform, eyeWhite, pupilMat, new Vector3(-0.14f, 0.62f, 0.62f));
            CreateEye(root.transform, eyeWhite, pupilMat, new Vector3(0.14f, 0.62f, 0.62f));

            // Bushy tail
            BuildTail(root.transform, furMat, darkMat);

            // Articulated legs (upper + lower)
            CreateLimb(root.transform, furMat, darkMat, new Vector3(-0.2f, 0.42f, 0.22f), true, -1, "LegFL");
            CreateLimb(root.transform, furMat, darkMat, new Vector3(0.2f, 0.42f, 0.22f), true, 1, "LegFR");
            CreateLimb(root.transform, furMat, darkMat, new Vector3(-0.18f, 0.4f, -0.38f), false, -1, "LegBL");
            CreateLimb(root.transform, furMat, darkMat, new Vector3(0.18f, 0.4f, -0.38f), false, 1, "LegBR");

            root.transform.localScale = Vector3.one * 1.05f;
            return root;
        }

        static void CreateEar(Transform parent, Material mat, Vector3 pos, float yaw)
        {
            var ear = CreateMeshPart(parent, "Ear", BuildEllipsoidMesh(0.05f, 0.09f, 0.04f, 8, 6), mat);
            ear.transform.localPosition = pos;
            ear.transform.localRotation = Quaternion.Euler(-20f, yaw, yaw * 0.4f);
        }

        static void CreateEye(Transform parent, Material white, Material pupil, Vector3 pos)
        {
            var eye = CreateMeshPart(parent, "Eye", BuildEllipsoidMesh(0.045f, 0.045f, 0.04f, 8, 6), white);
            eye.transform.localPosition = pos;
            var p = CreateMeshPart(eye.transform, "Pupil", BuildEllipsoidMesh(0.022f, 0.022f, 0.02f, 6, 6), pupil);
            p.transform.localPosition = new Vector3(0f, -0.005f, 0.025f);
        }

        static void BuildTail(Transform parent, Material fur, Material tipMat)
        {
            var tailRoot = new GameObject("Tail");
            tailRoot.transform.SetParent(parent, false);
            tailRoot.transform.localPosition = new Vector3(0f, 0.45f, -0.55f);
            tailRoot.transform.localRotation = Quaternion.Euler(-12f, 0f, 0f);

            float[] radii = { 0.16f, 0.2f, 0.22f, 0.18f, 0.12f, 0.07f };
            for (int i = 0; i < radii.Length; i++)
            {
                float t = i / (float)(radii.Length - 1);
                var seg = CreateMeshPart(
                    tailRoot.transform,
                    "TailSeg" + i,
                    BuildEllipsoidMesh(radii[i], radii[i] * 0.85f, radii[i] * 0.9f, 10, 8),
                    i > 3 ? tipMat : fur);
                seg.transform.localPosition = new Vector3(0f, Mathf.Sin(t * 0.6f) * 0.05f, -0.14f - t * 0.55f);
            }
        }

        static void CreateLimb(
            Transform parent,
            Material fur,
            Material pawMat,
            Vector3 hip,
            bool isFront,
            int side,
            string name)
        {
            var upperGo = new GameObject(name);
            upperGo.transform.SetParent(parent, false);
            upperGo.transform.localPosition = hip;

            float upperLen = isFront ? 0.22f : 0.2f;
            float lowerLen = isFront ? 0.2f : 0.18f;
            float thick = isFront ? 0.09f : 0.075f;

            var upperMesh = CreateMeshPart(
                upperGo.transform,
                "Upper",
                BuildEllipsoidMesh(thick, upperLen * 0.55f, thick * 0.9f, 10, 8),
                fur);
            upperMesh.transform.localPosition = new Vector3(0f, -upperLen * 0.45f, 0f);

            var lowerGo = new GameObject("Lower");
            lowerGo.transform.SetParent(upperGo.transform, false);
            lowerGo.transform.localPosition = new Vector3(0f, -upperLen * 0.9f, 0f);

            var lowerMesh = CreateMeshPart(
                lowerGo.transform,
                "LowerMesh",
                BuildEllipsoidMesh(thick * 0.85f, lowerLen * 0.5f, thick * 0.75f, 10, 8),
                fur);
            lowerMesh.transform.localPosition = new Vector3(0f, -lowerLen * 0.4f, 0.01f);

            var paw = CreateMeshPart(
                lowerGo.transform,
                "Paw",
                BuildEllipsoidMesh(thick * 1.1f, thick * 0.45f, thick * 1.3f, 8, 6),
                pawMat);
            paw.transform.localPosition = new Vector3(0f, -lowerLen * 0.85f, 0.03f);

            var leg = upperGo.AddComponent<AnteaterLeg>();
            leg.Configure(isFront, side, lowerGo.transform);
        }

        static GameObject CreateMeshPart(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            return go;
        }

        /// <summary>Arched, elongated torso mesh (forward +Z).</summary>
        static Mesh BuildBodyMesh()
        {
            // Radii along Z from rear to front.
            float[] r = { 0.2f, 0.26f, 0.3f, 0.3f, 0.28f, 0.24f, 0.2f };
            float length = 0.95f;
            return BuildLatheAlongZ(r, length, 14, 0.22f);
        }

        static Mesh BuildSnoutMesh()
        {
            float[] r = { 0.14f, 0.12f, 0.1f, 0.08f, 0.06f, 0.045f, 0.035f };
            return BuildLatheAlongZ(r, 0.72f, 12, 0f);
        }

        static Mesh BuildLatheAlongZ(float[] radii, float length, int sides, float arch)
        {
            int rings = radii.Length;
            var verts = new List<Vector3>(rings * sides);
            var norms = new List<Vector3>(rings * sides);
            var uvs = new List<Vector2>(rings * sides);
            var tris = new List<int>();

            for (int i = 0; i < rings; i++)
            {
                float t = i / (float)(rings - 1);
                float z = -length * 0.5f + t * length;
                float yOff = arch * Mathf.Sin(t * Mathf.PI);
                float rad = radii[i];
                for (int s = 0; s < sides; s++)
                {
                    float a = (s / (float)sides) * Mathf.PI * 2f;
                    var n = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    verts.Add(new Vector3(n.x * rad, n.y * rad * 0.85f + yOff, z));
                    norms.Add(n.normalized);
                    uvs.Add(new Vector2(s / (float)sides, t));
                }
            }

            for (int i = 0; i < rings - 1; i++)
            {
                for (int s = 0; s < sides; s++)
                {
                    int s2 = (s + 1) % sides;
                    int a = i * sides + s;
                    int b = i * sides + s2;
                    int c = (i + 1) * sides + s;
                    int d = (i + 1) * sides + s2;
                    tris.Add(a);
                    tris.Add(c);
                    tris.Add(d);
                    tris.Add(a);
                    tris.Add(d);
                    tris.Add(b);
                }
            }

            // Cap front & back
            AddDiskCap(verts, norms, uvs, tris, radii[0], -length * 0.5f, arch * 0f, sides, false);
            AddDiskCap(verts, norms, uvs, tris, radii[rings - 1], length * 0.5f, arch * 0f, sides, true);

            var mesh = new Mesh { name = "LatheZ" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static void AddDiskCap(
            List<Vector3> verts,
            List<Vector3> norms,
            List<Vector2> uvs,
            List<int> tris,
            float rad,
            float z,
            float yOff,
            int sides,
            bool front)
        {
            int center = verts.Count;
            verts.Add(new Vector3(0f, yOff, z));
            norms.Add(front ? Vector3.forward : Vector3.back);
            uvs.Add(new Vector2(0.5f, 0.5f));
            int start = verts.Count;
            for (int s = 0; s < sides; s++)
            {
                float a = (s / (float)sides) * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a) * rad, Mathf.Sin(a) * rad * 0.85f + yOff, z));
                norms.Add(front ? Vector3.forward : Vector3.back);
                uvs.Add(new Vector2(0.5f + Mathf.Cos(a) * 0.5f, 0.5f + Mathf.Sin(a) * 0.5f));
            }

            for (int s = 0; s < sides; s++)
            {
                int s2 = (s + 1) % sides;
                if (front)
                {
                    tris.Add(center);
                    tris.Add(start + s);
                    tris.Add(start + s2);
                }
                else
                {
                    tris.Add(center);
                    tris.Add(start + s2);
                    tris.Add(start + s);
                }
            }
        }

        static Mesh BuildEllipsoidMesh(float rx, float ry, float rz, int lon, int lat)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            for (int y = 0; y <= lat; y++)
            {
                float v = y / (float)lat;
                float phi = v * Mathf.PI;
                for (int x = 0; x <= lon; x++)
                {
                    float u = x / (float)lon;
                    float theta = u * Mathf.PI * 2f;
                    var n = new Vector3(
                        Mathf.Sin(phi) * Mathf.Cos(theta),
                        Mathf.Cos(phi),
                        Mathf.Sin(phi) * Mathf.Sin(theta));
                    verts.Add(new Vector3(n.x * rx, n.y * ry, n.z * rz));
                    norms.Add(n.normalized);
                    uvs.Add(new Vector2(u, v));
                }
            }

            for (int y = 0; y < lat; y++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int i0 = y * (lon + 1) + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + (lon + 1);
                    int i3 = i2 + 1;
                    tris.Add(i0);
                    tris.Add(i2);
                    tris.Add(i3);
                    tris.Add(i0);
                    tris.Add(i3);
                    tris.Add(i1);
                }
            }

            var mesh = new Mesh { name = "Ellipsoid" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Spawn logo billboard totem near world position.</summary>
        public static void SpawnLogoTotem(Vector3 worldPos, Texture2D logo)
        {
            var totem = new GameObject("LogoTotem");
            totem.transform.position = worldPos + Vector3.up * 1.4f;

            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole";
            pole.transform.SetParent(totem.transform, false);
            pole.transform.localPosition = new Vector3(0f, -0.4f, 0f);
            pole.transform.localScale = new Vector3(0.12f, 0.9f, 0.12f);
            // Wider solid on the totem root (visual pole mesh stays thin).
            Object.Destroy(pole.GetComponent<Collider>());
            var poleSolid = totem.AddComponent<CapsuleCollider>();
            poleSolid.radius = 0.2f;
            poleSolid.height = 2.2f;
            poleSolid.center = new Vector3(0f, -0.3f, 0f);
            poleSolid.direction = 1;
            pole.GetComponent<Renderer>().sharedMaterial =
                ProceduralMaterials.CreateLit(new Color(0.2f, 0.15f, 0.1f));

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Logo";
            quad.transform.SetParent(totem.transform, false);
            quad.transform.localPosition = new Vector3(0f, 0.7f, 0f);
            quad.transform.localScale = new Vector3(1.4f, 1.4f, 1f);
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            var mat = ProceduralMaterials.CreateUnlit(Color.white, logo);
            mat.renderQueue = 3000;
            quad.GetComponent<Renderer>().sharedMaterial = mat;
            quad.AddComponent<Billboard>();
        }
    }

    /// <summary>Marker for Meshy-generated visual (sampled walk clip and/or static mesh).</summary>
    public sealed class AnteaterGeneratedVisual : MonoBehaviour
    {
        public AnimationClip WalkClip { get; private set; }
        public GameObject AnimatedRoot { get; private set; }
        public bool HasSkinnedWalk { get; private set; }

        public void Configure(AnimationClip walkClip, GameObject animatedRoot, bool hasSkinnedWalk)
        {
            WalkClip = walkClip;
            AnimatedRoot = animatedRoot;
            HasSkinnedWalk = hasSkinnedWalk;
        }
    }

    /// <summary>Two-bone limb marker for procedural walk cycle.</summary>
    public sealed class AnteaterLeg : MonoBehaviour
    {
        public Vector3 BaseLocalEuler { get; private set; }
        public bool IsFront { get; private set; }
        public int Side { get; private set; }
        public Transform Lower { get; private set; }
        public Vector3 LowerBaseLocalEuler { get; private set; }

        public void Configure(bool isFront, int side, Transform lower)
        {
            IsFront = isFront;
            Side = side;
            Lower = lower;
            BaseLocalEuler = transform.localEulerAngles;
            if (Lower != null)
                LowerBaseLocalEuler = Lower.localEulerAngles;
        }
    }
}
