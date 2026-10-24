#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Bugsee.WrapperPolicy;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Bugsee.Editor
{
    /// <summary>
    /// The Editor's build-output scans on top of <see cref="DirectoryWalker"/>: one unreadable or over-long folder is
    /// warned about by name and the rest of the root is still scanned. Warnings carry the scanned root.
    /// </summary>
    static class BugseeDirectoryScan
    {
        static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

        internal static IEnumerable<string> Files(string root, string pattern, bool skipIntermediate = true) =>
            DirectoryWalker.EnumerateFiles(root, pattern, Warner(root), skipIntermediate, IsWindows);

        internal static IEnumerable<string> Directories(string root, string pattern, bool skipIntermediate = true) =>
            DirectoryWalker.EnumerateDirectories(root, pattern, Warner(root), skipIntermediate, IsWindows);

        internal static List<string> ChildDirectories(string dir) => DirectoryWalker.ChildDirectories(dir, Warner(dir));

        // Only the start of a build clears this, so scans run outside a build (none today) say a warning once per Editor session.
        // The scans of one build look at the same folders several times; each distinct warning is logged once per build.
        static readonly HashSet<string> Told = new HashSet<string>();

        internal static void ForgetWarnings()
        {
            lock (Told) Told.Clear();
        }

        static Action<string> Warner(string root) =>
            message =>
            {
                var line = $"Bugsee: scan of {root}: {message}";
                bool first;
                lock (Told) first = Told.Add(line);
                if (first) Debug.LogWarning(line);
            };
    }

    /// <summary>Starts each build with no warning remembered as already said.</summary>
    sealed class BugseeDirectoryScanReset : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MinValue;

        public void OnPreprocessBuild(BuildReport report) => BugseeDirectoryScan.ForgetWarnings();
    }
}
#endif
