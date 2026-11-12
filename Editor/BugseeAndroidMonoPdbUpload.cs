#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Bugsee.Editor
{
    /// <summary>
    /// A Release build on the Android Mono scripting backend ships no .pdb, so its managed frames are IL offsets
    /// (<c>[0x0000b] in &lt;module-guid&gt;:0</c>). This uploads the Portable PDBs Unity compiled, paired with the assemblies the
    /// app ships, with <c>bugsee-cli debug-files upload --type portable-pdb</c> (CLI 0.9.0+), so the backend resolves the frame
    /// to C# file:line. What is uploaded and when is <see cref="MonoPortablePdb"/>. Nothing here fails the build: a missing
    /// folder, an old CLI or a non-zero exit is a warning. Development builds ship their .pdb files and IL2CPP has its own
    /// line map, so neither is touched.
    /// </summary>
    public sealed class BugseeAndroidMonoPdbUpload : IPostprocessBuildWithReport
    {
        static bool _warned;

        public int callbackOrder => 900;

        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                if (report == null || report.summary.platform != BuildTarget.Android)
                {
                    return;
                }

                var backend = BugseeBuildIdentity.AndroidNameSettings(report).Backend;
                var development = (report.summary.options & BuildOptions.Development) != 0;
                var outcome = MonoPortablePdb.Decide(backend, development,
                    Environment.GetEnvironmentVariable("BUGSEE_NO_SYMBOL_UPLOAD") != null, BugseeCliRunner.HasAppToken);
                if (outcome == MonoPortablePdb.Outcome.NotApplicable)
                {
                    return;
                }

                if (outcome != MonoPortablePdb.Outcome.Upload)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Debug.LogWarning(MonoPortablePdb.NotUploadedNotice(outcome));
                    }

                    return;
                }

                Upload(BugseeBuildIdentity.ResolveVersion(report), BugseeBuildIdentity.ResolveBuildNumber(report));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Bugsee: uploading the Portable PDBs of the Mono build failed: {ex.Message} — build continues.");
            }
        }

        static void Upload(string version, string build)
        {
            var artifacts = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "Library", "Bee", "artifacts");
            var candidates = new List<CompilerOutputDirectory>();
            if (Directory.Exists(artifacts))
            {
                foreach (var dir in BugseeDirectoryScan.ChildDirectories(artifacts))
                {
                    // A project whose scripts all live in asmdefs has no Assembly-CSharp.dll: any assembly with a pdb beside it counts.
                    DateTime? written = null;
                    try
                    {
                        foreach (var dll in Directory.GetFiles(dir, "*.dll"))
                        {
                            if (!File.Exists(Path.ChangeExtension(dll, ".pdb")))
                            {
                                continue;
                            }

                            var time = File.GetLastWriteTimeUtc(dll);
                            if (written == null || time > written.Value) written = time;
                        }
                    }
                    catch { /* unreadable: not a candidate */ }
                    var android = false;
                    try
                    {
                        foreach (var rsp in Directory.GetFiles(dir, "*.rsp"))
                        {
                            if (rsp.EndsWith(".mvfrm.rsp", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            android = MonoPortablePdb.DefinesAndroid(File.ReadAllText(rsp));
                            break;
                        }
                    }
                    catch { /* unreadable: not a candidate */ }

                    candidates.Add(new CompilerOutputDirectory { Name = Path.GetFileName(dir), Path = dir, AssemblyWriteUtc = written, TargetsAndroid = android });
                }
            }

            var compiled = MonoPortablePdb.PickCompilerOutput(candidates);
            if (compiled == null)
            {
                Debug.LogWarning("Bugsee: no Release player compiler output (Library/Bee/artifacts/<prefix>P.dag compiled for Android) found, so the Portable PDBs of " +
                                 "this Mono build are not uploaded and its managed frames stay IL offsets.");
                return;
            }

            // The assemblies the app is made from (the linker's output): their module ids are the ones the runtime prints.
            var shipped = Path.Combine(artifacts, "Android", "ManagedStripped");
            var shippedDir = Directory.Exists(shipped) ? shipped : null;
            if (shippedDir == null)
            {
                Debug.LogWarning($"Bugsee: {shipped} not found: the Portable PDBs are uploaded under the compiler's module ids. That matches the app only " +
                                 "when managed stripping is Disabled; with Low or higher the frames of this build stay IL offsets.");
            }

            var tooOld = MonoPortablePdb.CliTooOld(BugseeCliLocator.Resolve().Version);
            if (tooOld != null)
            {
                Debug.LogWarning("Bugsee: " + tooOld);
                return;
            }

            Debug.Log($"Bugsee: uploading the Portable PDBs of the Mono build ({compiled.Name}) via {BugseeCliRunner.CliPath} …");
            var exit = BugseeCliRunner.Run(MonoPortablePdb.Arguments(compiled.Path, shippedDir, version, build));
            if (exit != 0)
            {
                Debug.LogWarning($"Bugsee: bugsee-cli portable-pdb upload {BugseeCliPolicy.DescribeExit(exit)} — managed frames of this Mono build stay IL offsets.");
            }
        }
    }
}
#endif
