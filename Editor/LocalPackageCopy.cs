using System;
using System.Diagnostics;
using System.IO;

namespace Bugsee.Editor
{
    /// <summary>
    /// Copying the local iOS SDK package into the exported Xcode project. Free of Unity types so it can be tested with the
    /// package's other policy code; every failure is an exception the export turns into a failed build.
    /// </summary>
    public static class LocalPackageCopy
    {
        public const string Marker = ".bugsee-local-package";

        /// <summary>Where warnings (a staging folder that could not be removed) go; the export points it at the Unity console.</summary>
        public static Action<string> Warn = _ => { };

        /// <summary>Why <paramref name="packageDir"/> cannot be used as the SDK package, or null when it can.</summary>
        public static string Problem(string packageDir)
        {
            if (!File.Exists(Path.Combine(packageDir, "Package.swift")))
            {
                return "no Package.swift there.";
            }

            string framework = Path.Combine(packageDir, "Bugsee.xcframework");
            if (!Directory.Exists(framework))
            {
                return "no Bugsee.xcframework there.";
            }

            // The bridge imports <Bugsee/BugseeSPI.h>, which iOS SDK 7.0.0 introduced: an older package (7.0.0-betaN) would
            // fail in Xcode with a missing header instead of here.
            if (Directory.GetFiles(framework, "BugseeSPI.h", SearchOption.AllDirectories).Length == 0)
            {
                return "Bugsee.xcframework has no BugseeSPI.h: it is older than iOS SDK 7.0.0, which the bridge needs.";
            }

            // Every target path the manifest names must exist, or Xcode refuses the package ("invalid custom path").
            string manifest = StripLineComments(File.ReadAllText(Path.Combine(packageDir, "Package.swift")));
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(manifest, "path:\\s*\"([^\"]+)\""))
            {
                string relative = match.Groups[1].Value;
                string full = Path.Combine(packageDir, relative);
                if (!Directory.Exists(full) && !File.Exists(full))
                {
                    return $"Package.swift names the target path '{relative}', which the package does not have.";
                }
            }

            return null;
        }

        static string StripLineComments(string text)
        {
            var builder = new System.Text.StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                builder.Append(comment >= 0 ? line.Substring(0, comment) : line).Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Copies the package into a staging folder next to <paramref name="destination"/> (marker first), checks it, and only
        /// then replaces the old copy: the source is read before anything is deleted, a copy that fails half way never lands,
        /// and the staging folder is removed whatever happens (read-only files included).
        /// </summary>
        /// <param name="selective">Copy only Package.swift, Bugsee.xcframework and Sources (every target path the manifest names: BugseeSwiftUI, and BugseeCrashExtension since 7.0.0; an assembled package);
        /// otherwise the whole folder (the in-package stub).</param>
        public static void CopyAndSwapIn(string source, string destination, bool selective)
        {
            string staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, Marker), "Copied by the Bugsee iOS export; removed when the export links the published package again.\n");
                if (selective)
                {
                    CopySelected(source, staging);
                }
                else
                {
                    CopyAll(source, staging);
                }

                string copied = Problem(staging);
                if (copied != null)
                {
                    throw new InvalidOperationException($"Copying the local iOS SDK package {source} left no usable package: {copied}");
                }

                // The old copy is moved aside, not deleted, until the new one is in place, so a failed move keeps it.
                string backup = null;
                if (Directory.Exists(destination))
                {
                    backup = destination + ".old-" + Guid.NewGuid().ToString("N");
                    Directory.Move(destination, backup);
                }

                try
                {
                    Directory.Move(staging, destination);
                }
                catch
                {
                    if (backup != null && !Directory.Exists(destination))
                    {
                        Directory.Move(backup, destination);
                    }

                    throw;
                }

                if (backup != null)
                {
                    DeleteTree(backup, bestEffort: true);
                }
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    DeleteTree(staging, bestEffort: true);
                }
            }
        }

        /// <summary>Deletes a tree whose files may be read-only (a package copied from a read-only cache or mount).</summary>
        public static void DeleteTree(string path, bool bestEffort = false)
        {
            try
            {
                RunTool("/bin/chmod", new[] { "-R", "u+w", path }, ignoreFailure: true);
                Directory.Delete(path, true);
            }
            catch (Exception e) when (bestEffort)
            {
                Warn($"Could not remove {path}: {e.Message}");
            }
        }

        /// <summary>The directory's real path (symlinks and the volume's case resolved), or its full path when that cannot be told.</summary>
        public static string PhysicalPath(string directory)
        {
            string full = Path.GetFullPath(directory);
            string output = RunTool("/bin/sh", new[] { "-c", "cd \"$1\" && pwd -P", "sh", full }, ignoreFailure: true);
            return string.IsNullOrWhiteSpace(output) ? full : output.Trim();
        }

        // cp -RP keeps the symlinks inside the xcframework's frameworks, which a file-by-file copy would turn into duplicate
        // trees, and works from a read-only source (ditto does not); the copy is made writable afterwards.
        static void CopySelected(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string item in new[] { "Package.swift", "Bugsee.xcframework", "Sources" })
            {
                string source = Path.Combine(sourceDir, item);
                if (!File.Exists(source) && !Directory.Exists(source))
                {
                    continue;
                }

                string dest = Path.Combine(destDir, item);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                // A Sources that is itself a symlink to a folder in the package would copy as a dangling link: copy what it
                // points at (the trailing /. follows only that top-level link; links inside the tree stay links).
                bool linkedFolder = item == "Sources" && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0;
                if (linkedFolder)
                {
                    Directory.CreateDirectory(dest);
                    RunTool("/bin/cp", new[] { "-RP", source + "/.", dest });
                }
                else
                {
                    RunTool("/bin/cp", new[] { "-RP", source, dest });
                }
            }

            RunTool("/bin/chmod", new[] { "-R", "u+w", destDir });
        }

        static void CopyAll(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") ||
                    Path.GetFileName(file) == ".DS_Store")
                {
                    continue;
                }

                string relative = file.Substring(sourceDir.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destFile = Path.Combine(destDir, relative);
                string destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                {
                    Directory.CreateDirectory(destParent);
                }

                File.Copy(file, destFile, true);
            }
        }

        /// <summary>Runs a tool and returns its standard output; a failure throws unless <paramref name="ignoreFailure"/>.</summary>
        static string RunTool(string tool, string[] arguments, bool ignoreFailure = false)
        {
            try
            {
                var info = new ProcessStartInfo(tool)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string argument in arguments)
                {
                    info.ArgumentList.Add(argument);
                }

                using (var process = Process.Start(info))
                {
                    string error = process.StandardError.ReadToEndAsync().Result;
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0 && !ignoreFailure)
                    {
                        throw new IOException($"{tool} {string.Join(" ", arguments)} failed: {error}");
                    }

                    return output;
                }
            }
            catch (Exception e) when (ignoreFailure && !(e is IOException))
            {
                return "";
            }
        }
    }
}
