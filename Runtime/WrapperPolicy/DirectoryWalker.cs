using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Bugsee.WrapperPolicy
{
    /// <summary>The directory listing the walker needs, one folder at a time. Real implementation: <see cref="DirectoryWalker.Disk"/>.</summary>
    public interface IWalkFileSystem
    {
        /// <summary>Full paths of the files directly in <paramref name="dir"/> matching <paramref name="pattern"/>. May throw, also while enumerating.</summary>
        IEnumerable<string> Files(string dir, string pattern);

        /// <summary>Full paths of the folders directly in <paramref name="dir"/> matching <paramref name="pattern"/> ("*" for all). May throw, also while enumerating.</summary>
        IEnumerable<string> Directories(string dir, string pattern);

        /// <summary>The absolute target when <paramref name="dir"/> is a symbolic link or junction, null when it is not or cannot be told.</summary>
        string LinkTarget(string dir);
    }

    /// <summary>
    /// A scan of a build output tree that survives one bad folder. <c>Directory.EnumerateFiles(root, ..., AllDirectories)</c>
    /// stops the whole root at the first folder it cannot read, or (on runtimes that skip it) hides that folder without a
    /// word. This walks one level at a time instead: a folder that cannot be listed is reported through
    /// <c>warn</c> and its siblings are still visited; so is a folder whose path is 248 characters or more on Windows
    /// (the directory limit; the Editor's Mono lists such folders but cannot read the files in them the usual way).
    ///
    /// Order is deterministic: a folder's files, then its sub-folders, each in ordinal order, depth first. Results are
    /// spelled under the root as passed (links are followed, as <c>AllDirectories</c> does, with a guard against cycles).
    /// With <c>skipIntermediate</c> the folders <see cref="BuildPathExclusion"/> excludes below the root are not entered
    /// at all, which gives the same results as filtering the paths afterwards and also spares their (often huge) trees.
    /// No <c>\\?\</c> prefixing: that is for reads (<see cref="LongPathPolicy"/>), not for listing.
    /// </summary>
    public static class DirectoryWalker
    {
        /// <summary>A directory path from this length on is where Windows' limit for a folder (248) is reached.</summary>
        public const int LongFolderFrom = 248;

        /// <summary>
        /// A directory path from this length on is where the path limit (260) is reached: the Editor's Mono lists such a
        /// folder but cannot open the files in it with the plain file calls.
        /// </summary>
        public const int UnreadableFrom = 260;

        /// <summary>Depth below the root where the walk stops (a backstop for loops that cannot be recognised).</summary>
        public const int MaxDepth = 64;

        /// <summary>Warnings per walk; the rest is summarised in one line.</summary>
        public const int MaxWarnings = 20;

        public static readonly IWalkFileSystem Disk = new DiskFileSystem();

        /// <summary>The files named by <paramref name="pattern"/> anywhere below <paramref name="root"/>.</summary>
        public static IEnumerable<string> EnumerateFiles(
            string root, string pattern, Action<string> warn, bool skipIntermediate = true,
            bool warnLongPaths = true, IWalkFileSystem fs = null)
            => Walk(root, pattern, null, warn, skipIntermediate, warnLongPaths, fs ?? Disk);

        /// <summary>The folders named by <paramref name="pattern"/> anywhere below <paramref name="root"/> (the walk still goes inside them).</summary>
        public static IEnumerable<string> EnumerateDirectories(
            string root, string pattern, Action<string> warn, bool skipIntermediate = true,
            bool warnLongPaths = true, IWalkFileSystem fs = null)
            => Walk(root, null, pattern, warn, skipIntermediate, warnLongPaths, fs ?? Disk);

        /// <summary>The folders directly in <paramref name="dir"/>, sorted; a failure is warned about and gives an empty list.</summary>
        public static List<string> ChildDirectories(string dir, Action<string> warn, IWalkFileSystem fs = null)
        {
            var list = new List<string>();
            var failure = Collect(() => (fs ?? Disk).Directories(dir, "*"), list);
            if (failure != null)
            {
                warn?.Invoke("could not list the folder " + dir + ": " + failure.Message);
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        sealed class Frame
        {
            public string Path;
            public int Depth;
            public int ParentLevel;
            public Frame Parent;
            public string LinkTarget;
        }

        sealed class Budget
        {
            readonly Action<string> _warn;
            int _count;
            int _dropped;

            public Budget(Action<string> warn) { _warn = warn; }

            public void Warn(string message)
            {
                if (_warn == null) return;
                if (_count < MaxWarnings) { _count++; _warn(message); }
                else _dropped++;
            }

            public void Done()
            {
                if (_dropped > 0 && _warn != null)
                {
                    _warn(_dropped + " more folder warnings from this scan were left out");
                }
            }
        }

        static IEnumerable<string> Walk(
            string root, string filePattern, string dirPattern, Action<string> warn,
            bool skipIntermediate, bool warnLongPaths, IWalkFileSystem fs)
        {
            if (string.IsNullOrEmpty(root)) yield break;

            var budget = new Budget(warn);
            var stack = new Stack<Frame>();
            stack.Push(new Frame { Path = root, Depth = 0 });
            var files = new List<string>();
            var dirs = new List<string>();
            var matched = new List<string>();
            var depthWarned = false;
            try
            {
                while (stack.Count > 0)
                {
                    var frame = stack.Pop();
                    // Warned once where a branch first reaches 248 characters and once more where it reaches 260,
                    // not for every folder below.
                    var level = warnLongPaths ? LengthLevel(frame.Path) : 0;
                    if (level == 1 && frame.ParentLevel < 1)
                    {
                        budget.Warn("the folder path is " + frame.Path.Length + " characters (" + LongFolderFrom
                            + " or more), over what Windows allows for a folder: " + frame.Path);
                    }
                    else if (level == 2 && frame.ParentLevel < 2)
                    {
                        budget.Warn("the folder path is " + frame.Path.Length + " characters (" + UnreadableFrom
                            + " or more), the files below it cannot be opened with the plain file calls on Windows"
                            + " (they are read through the extended-length path where the Editor needs them): " + frame.Path);
                    }

                    files.Clear(); dirs.Clear(); matched.Clear();
                    Exception failure = null;
                    if (filePattern != null)
                    {
                        failure = Collect(() => fs.Files(frame.Path, filePattern), files);
                    }
                    var dirFailure = Collect(() => fs.Directories(frame.Path, "*"), dirs);
                    if (failure == null) failure = dirFailure;
                    if (dirPattern != null)
                    {
                        var matchFailure = Collect(() => fs.Directories(frame.Path, dirPattern), matched);
                        if (failure == null) failure = matchFailure;
                    }

                    if (failure != null)
                    {
                        budget.Warn("could not fully list the folder " + frame.Path + " (" + failure.GetType().Name + "): "
                            + failure.Message + "; the rest of the scan continues");
                    }

                    files.Sort(StringComparer.Ordinal);
                    foreach (var file in files) yield return file;

                    matched.Sort(StringComparer.Ordinal);
                    foreach (var match in matched) yield return match;

                    dirs.Sort(StringComparer.Ordinal);
                    if (frame.Depth >= MaxDepth)
                    {
                        if (dirs.Count > 0 && !depthWarned)
                        {
                            depthWarned = true;
                            budget.Warn("the folders below " + frame.Path + " are nested more than " + MaxDepth
                                + " levels deep (a link loop?) and are not scanned");
                        }
                        continue;
                    }

                    // Reverse, so the stack pops them in ordinal order.
                    for (var i = dirs.Count - 1; i >= 0; i--)
                    {
                        var child = dirs[i];
                        if (skipIntermediate && BuildPathExclusion.IsExcludedFolderName(LastSegment(child))) continue;

                        var next = new Frame { Path = child, Depth = frame.Depth + 1, ParentLevel = level, Parent = frame };
                        string target = null;
                        try { target = fs.LinkTarget(child); } catch { /* not a link we can tell */ }
                        if (!string.IsNullOrEmpty(target))
                        {
                            if (LoopsBack(frame, target))
                            {
                                budget.Warn("the folder " + child + " is a link back to " + target + " and is not scanned again");
                                continue;
                            }
                            next.LinkTarget = Normalize(target);
                        }
                        stack.Push(next);
                    }
                }
            }
            finally
            {
                budget.Done();
            }
        }

        static int LengthLevel(string path) =>
            path.Length >= UnreadableFrom ? 2 : path.Length >= LongFolderFrom ? 1 : 0;

        // A link whose target is the folder it sits in or one of its ancestors, or a target a link above already led to.
        static bool LoopsBack(Frame parent, string target)
        {
            var t = Normalize(target);
            for (var f = parent; f != null; f = f.Parent)
            {
                var p = Normalize(f.Path);
                if (t.Length <= p.Length && p.StartsWith(t, StringComparison.Ordinal)
                    && (p.Length == t.Length || p[t.Length] == '/' || t.EndsWith("/", StringComparison.Ordinal)))
                {
                    return true;
                }
                if (f.LinkTarget != null && f.LinkTarget == t) return true;
            }
            return false;
        }

        static string Normalize(string path)
        {
            var p = path.Replace('\\', '/');
            while (p.Length > 1 && p.EndsWith("/", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 1);
            return p;
        }

        static string LastSegment(string path)
        {
            var p = path.TrimEnd('/', '\\');
            var i = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
            return i < 0 ? p : p.Substring(i + 1);
        }

        // Everything the listing yields before it fails is kept; the failure, if any, is returned.
        static Exception Collect(Func<IEnumerable<string>> list, List<string> into)
        {
            try
            {
                foreach (var item in list()) into.Add(item);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        sealed class DiskFileSystem : IWalkFileSystem
        {
            // FileSystemInfo.ResolveLinkTarget exists from .NET 6; the Editor's Mono lacks it and then reports no links.
            static readonly MethodInfo Resolve = typeof(FileSystemInfo).GetMethod(
                "ResolveLinkTarget", new[] { typeof(bool) });

            public IEnumerable<string> Files(string dir, string pattern) =>
                Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);

            public IEnumerable<string> Directories(string dir, string pattern) =>
                Directory.EnumerateDirectories(dir, pattern, SearchOption.TopDirectoryOnly);

            public string LinkTarget(string dir)
            {
                if (Resolve == null) return null;
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return null;
                var target = Resolve.Invoke(info, new object[] { true }) as FileSystemInfo;
                return target?.FullName;
            }
        }
    }
}
