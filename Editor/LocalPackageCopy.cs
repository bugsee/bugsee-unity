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

            if (!Directory.Exists(Path.Combine(packageDir, "Bugsee.xcframework")))
            {
                return "no Bugsee.xcframework there.";
            }

            return null;
        }

        /// <summary>
        /// Copies the package into a staging folder next to <paramref name="destination"/> (marker first), checks it, and only
        /// then replaces the old copy: the source is read before anything is deleted, a copy that fails half way never lands,
        /// and the staging folder is removed whatever happens (read-only files included).
        /// </summary>
        /// <param name="selective">Copy only Package.swift, Bugsee.xcframework and Sources/BugseeSwiftUI (an assembled package);
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
            foreach (string item in new[] { "Package.swift", "Bugsee.xcframework", Path.Combine("Sources", "BugseeSwiftUI") })
            {
                string source = Path.Combine(sourceDir, item);
                if (!File.Exists(source) && !Directory.Exists(source))
                {
                    continue;
                }

                string dest = Path.Combine(destDir, item);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                RunTool("/bin/cp", new[] { "-RP", source, dest });
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
