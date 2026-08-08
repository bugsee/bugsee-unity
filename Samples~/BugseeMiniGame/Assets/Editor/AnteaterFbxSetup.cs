#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Bugsee.Sample.Editor
{
    /// <summary>
    /// Imports Meshy quadruped FBX as Generic (root motion off) and builds a
    /// Resources AnimatorController. Runtime drives Walk normalized time in code
    /// (Speed param kept for Inspector/tools; not required for device playback).
    /// </summary>
    public sealed class AnteaterFbxSetup : AssetPostprocessor
    {
        const string FbxResourcePath = "Assets/Resources/Anteater/ScarletSnout.fbx";
        const string FbxArtPath = "Assets/Art/Anteater/Generated/ScarletSnout.fbx";
        const string ControllerPath = "Assets/Resources/Anteater/ScarletSnoutAnim.controller";
        const string WalkClipPath = "Assets/Resources/Anteater/ScarletSnoutWalk.anim";
        const string NormalArtPath = "Assets/Art/Anteater/Generated/ScarletSnout_Normal.png";

        void OnPreprocessModel()
        {
            if (!IsScarletSnoutModel(assetPath))
                return;

            var importer = (ModelImporter)assetImporter;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            importer.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
            importer.meshCompression = ModelImporterMeshCompression.Medium;
            importer.isReadable = false;
            importer.optimizeMeshPolygons = true;
            importer.optimizeMeshVertices = true;
            // CharacterController is authoritative — do not bake root motion into gameplay.
            importer.motionNodeName = "";

            // Force looping on all takes (Meshy Walk is one-shot by default otherwise).
            var takes = importer.defaultClipAnimations;
            if (takes != null && takes.Length > 0)
            {
                for (int i = 0; i < takes.Length; i++)
                {
                    takes[i].loopTime = true;
                    takes[i].cycleOffset = 0f;
                }

                importer.clipAnimations = takes;
            }
        }

        void OnPreprocessTexture()
        {
            if (assetPath != NormalArtPath && !assetPath.EndsWith("ScarletSnout_Normal.png"))
                return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.NormalMap;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = 1024;
        }

        static void OnPostprocessAllAssets(
            string[] imported,
            string[] deleted,
            string[] movedTo,
            string[] movedFrom)
        {
            bool missingController = !File.Exists(ControllerPath) &&
                                     (File.Exists(FbxResourcePath) || File.Exists(FbxArtPath));
            bool touched = imported.Any(IsScarletSnoutModel) ||
                           movedTo.Any(IsScarletSnoutModel) ||
                           missingController;
            if (!touched || _rebuilding)
                return;

            EnsureAnimatorController();
        }

        [MenuItem("Bugsee/MiniGame/Rebuild Anteater Animator")]
        static void RebuildMenu()
        {
            EnsureAnimatorController();
            Debug.Log("[AnteaterFbxSetup] Animator controller rebuilt.");
        }

        static bool IsScarletSnoutModel(string path)
        {
            return path != null &&
                   path.EndsWith("ScarletSnout.fbx") &&
                   (path == FbxResourcePath || path == FbxArtPath || path.Contains("/Anteater/"));
        }

        static bool _rebuilding;

        static void EnsureAnimatorController()
        {
            if (_rebuilding)
                return;

            _rebuilding = true;
            try
            {
                EnsureAnimatorControllerUnguarded();
            }
            finally
            {
                _rebuilding = false;
            }
        }

        static void EnsureAnimatorControllerUnguarded()
        {
            string fbxPath = File.Exists(FbxResourcePath) ? FbxResourcePath :
                File.Exists(FbxArtPath) ? FbxArtPath : null;
            if (fbxPath == null)
                return;

            var clips = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<AnimationClip>()
                .Where(c => c != null && !c.name.StartsWith("__preview__", System.StringComparison.Ordinal))
                .ToList();
            if (clips.Count == 0)
            {
                Debug.LogWarning("[AnteaterFbxSetup] No AnimationClips on " + fbxPath);
                return;
            }

            // Prefer a clip whose name mentions walk; otherwise first clip.
            // Meshy often names the take "Armature|...|Unreal Take|baselayer" (no "walk").
            var sourceWalk = clips.FirstOrDefault(c =>
                                c.name.IndexOf("walk", System.StringComparison.OrdinalIgnoreCase) >= 0)
                            ?? clips[0];

            // FBX sub-asset loopTime is unreliable — bake a standalone looping .anim.
            var walk = BakeLoopingWalkClip(sourceWalk);

            // Also stamp ModelImporter so Inspector shows Loop.
            var modelImporter = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (modelImporter != null)
            {
                var takes = modelImporter.defaultClipAnimations;
                if (takes != null && takes.Length > 0)
                {
                    for (int i = 0; i < takes.Length; i++)
                        takes[i].loopTime = true;
                    modelImporter.clipAnimations = takes;
                    EditorUtility.SetDirty(modelImporter);
                    // Avoid SaveAndReimport here (re-entrancy); user can reimport if needed.
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ControllerPath) ?? "Assets/Resources/Anteater");

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            }

            // Reset parameters / states to a known Idle/Walk Speed setup.
            while (controller.parameters.Length > 0)
                controller.RemoveParameter(0);

            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

            var sm = controller.layers[0].stateMachine;
            var existing = sm.states.Select(s => s.state).ToList();
            foreach (var state in existing)
                sm.RemoveState(state);

            var walkState = sm.AddState("Walk");
            walkState.motion = walk;
            walkState.speedParameterActive = true;
            walkState.speedParameter = "Speed";
            sm.defaultState = walkState;

            // Assign controller onto the FBX model Animator so Instantiate already works.
            var modelRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (modelRoot != null)
            {
                var animator = modelRoot.GetComponent<Animator>();
                if (animator != null)
                {
                    animator.applyRootMotion = false;
                    animator.runtimeAnimatorController = controller;
                    EditorUtility.SetDirty(modelRoot);
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log("[AnteaterFbxSetup] Walk clip='" + walk.name + "' loop=" +
                      AnimationUtility.GetAnimationClipSettings(walk).loopTime + " → " + ControllerPath);
        }

        /// <summary>
        /// Copy the FBX take into a writable .anim with loopTime forced on. Imported FBX
        /// sub-clips often ignore AnimationUtility loop edits.
        /// </summary>
        static AnimationClip BakeLoopingWalkClip(AnimationClip source)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WalkClipPath) ?? "Assets/Resources/Anteater");

            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(WalkClipPath);
            if (existing != null)
                AssetDatabase.DeleteAsset(WalkClipPath);

            var walk = new AnimationClip();
            EditorUtility.CopySerialized(source, walk);
            walk.name = "ScarletSnoutWalk";
            walk.wrapMode = WrapMode.Loop;

            var settings = AnimationUtility.GetAnimationClipSettings(walk);
            settings.loopTime = true;
            settings.loopBlend = true;
            AnimationUtility.SetAnimationClipSettings(walk, settings);

            AssetDatabase.CreateAsset(walk, WalkClipPath);
            EditorUtility.SetDirty(walk);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(WalkClipPath) ?? walk;
        }
    }
}
#endif
